using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoCommit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

public class GenerationTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public string Body = "";
        public string Url = "";
        public readonly List<string> Bodies = new();
        public string Reply = "{\"choices\":[{\"message\":{\"content\":\"fix: 修复错误\"},\"finish_reason\":\"stop\"}]}";
        public Func<int, HttpStatusCode>? StatusForCall;
        public Action<int>? OnCall;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(); Url = request.RequestUri!.ToString(); Bodies.Add(Body);
            OnCall?.Invoke(Calls);
            return new HttpResponseMessage(StatusForCall?.Invoke(Calls) ?? HttpStatusCode.OK) { Content = new StringContent(Reply) };
        }
    }
    [Fact]
    public async Task ReportsProviderUsageWithoutInventingMissingCounts()
    {
        var handler = new Handler(); using var client = new HttpClient(handler);
        var generator = new CommitGenerator(client);
        var settings = new ModelSettings { Model = "local", AllowHttp = true };
        var missing = await generator.TestConnectionAsync(settings, "", default);
        Assert.Null(missing.InputTokens); Assert.Null(missing.TotalTokens);
        handler.Reply = "{\"model\":\"actual\",\"usage\":{\"prompt_tokens\":101,\"completion_tokens\":20,\"total_tokens\":121,\"prompt_tokens_details\":{\"cached_tokens\":30},\"completion_tokens_details\":{\"reasoning_tokens\":5}},\"choices\":[{\"message\":{\"content\":\"OK\"}}]}";
        var usage = await generator.TestConnectionAsync(settings, "", default);
        Assert.Equal(101, usage.InputTokens); Assert.Equal(20, usage.OutputTokens);
        Assert.Equal(121, usage.TotalTokens); Assert.Equal(30, usage.CachedTokens);
        Assert.Equal(5, usage.ReasoningTokens); Assert.Equal("actual", usage.Model);
    }
    [Fact]
    public async Task ConnectionTestUsesFixedTextWithoutRepositoryData()
    {
        var handler = new Handler(); using var client = new HttpClient(handler);
        await new CommitGenerator(client).TestConnectionAsync(new ModelSettings { BaseUrl = "https://example.test/v1", Model = "local" }, "", default);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("Reply OK.", handler.Body);
        Assert.DoesNotContain("stagedDiff", handler.Body);
        Assert.DoesNotContain("commitRules", handler.Body);
    }
    [Fact]
    public async Task SendsRulesAndDiffToConfiguredEndpoint()
    {
        var handler = new Handler(); using var client = new HttpClient(handler);
        var result = await new CommitGenerator(client).GenerateAsync(new ChangeSnapshot("repo", "+changed"), "RULE_MARKER",
            new ModelSettings { BaseUrl = "https://example.test/v1", Model = "local" }, "", default);
        Assert.Equal("fix: 修复错误", result.Text); Assert.Equal("https://example.test/v1/chat/completions", handler.Url);
        Assert.Contains("RULE_MARKER", handler.Body); Assert.Contains("+changed", handler.Body);
    }

    [Fact]
    public async Task TfvcBatchesUseFullCaptureAndCombineWithinBudget()
    {
        var changes = new[] {
            new TfvcFileChange("文件：$/large.cpp", hasOmittedHunks: true),
            new TfvcFileChange("文件：$/binary；文件超过 1 MiB")
        };
        var snapshot = new ChangeSnapshot("repo", "", context: "full-hash", tfvcChanges: changes);
        var validationSnapshot = new ChangeSnapshot("repo", "", context: "full-hash");
        Assert.Equal(snapshot.Fingerprint, validationSnapshot.Fingerprint);

        var handler = new Handler { Reply = "{\"model\":\"actual\",\"choices\":[{\"message\":{\"content\":\"变更事实\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3}}" };
        using var client = new HttpClient(handler);
        var generator = new CommitGenerator(client);
        var settings = new ModelSettings { BaseUrl = "https://example.test/v1", Model = "local", MaxInputCharacters = 500 };
        const string rules = "标题包含引号 \" 和反斜线 \\。";
        using var capture = new StringReader("文件：$/large.cpp\n@@ -1,1 +1,1 @@\n+" + new string('"', 1100) +
            "🚀\n+NOT_IN_PREVIEW\n文件：$/binary；文件超过 1 MiB\n");
        Assert.Contains("分批", CommitGenerator.PreviewDiff(snapshot, rules, settings.MaxInputCharacters));
        var result = await generator.GenerateAsync(snapshot, rules, settings, "", default, capture);
        var payloads = handler.Bodies.Select(body => ReadPayload(body, settings.MaxInputCharacters)).ToArray();
        var leaves = payloads.Select(payload => (string?)payload["stagedDiff"]).Where(diff => diff != null).ToArray();
        Assert.True(leaves.Length >= 4);
        Assert.Contains(leaves, diff => diff!.Contains("NOT_IN_PREVIEW"));
        Assert.Contains(leaves, diff => diff!.Contains("🚀"));
        Assert.Contains(leaves, diff => diff!.Contains("$/binary"));
        Assert.Equal(1100, leaves.Sum(diff => diff!.Count(c => c == '"')));
        Assert.Contains(payloads.Take(payloads.Length - 1), payload => payload["changeSummaries"] != null);
        Assert.Equal(rules, (string?)payloads[^1]["commitRules"]);
        Assert.Equal("变更事实", result.Text);
        Assert.Null(result.FallbackReason);
        Assert.Equal(handler.Calls, result.Requests);
        Assert.Equal(handler.Calls * 12, result.InputTokens);
        Assert.Null(result.TotalTokens);
    }

    [Fact]
    public async Task OversizedRulesUseLocalSummaryWithoutMakingRequests()
    {
        var snapshot = new ChangeSnapshot("repo", "", files: new[] { new ChangeFile("$/included.txt", "修改") },
            tfvcChanges: new[] { new TfvcFileChange("文件：$/included.txt") });
        var handler = new Handler(); using var client = new HttpClient(handler);
        var result = await new CommitGenerator(client).GenerateAsync(snapshot, new string('规', 500),
            new ModelSettings { Model = "local", AllowHttp = true, MaxInputCharacters = 500 }, "", default);
        Assert.Contains("输入预算", result.FallbackReason);
        Assert.Contains("修改 $/included.txt", result.Text);
        Assert.Equal(0, handler.Calls);
    }
    [Theory]
    [InlineData("", 48000, true)]
    [InlineData("+change", 0, true)]
    [InlineData("+change", 48000, false)]
    public async Task InvalidInputDoesNotSendCode(string diff, int budget, bool allowHttp)
    {
        var handler = new Handler(); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CommitGenerator(client).GenerateAsync(new ChangeSnapshot("repo", diff), "rules",
            new ModelSettings { Model = "test", AllowHttp = allowHttp, MaxInputCharacters = budget }, "", default));
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task TruncatedOutputUsesLocalSummary()
    {
        var handler = new Handler { Reply = "{\"choices\":[{\"message\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}" };
        using var client = new HttpClient(handler);
        var result = await new CommitGenerator(client).GenerateAsync(new ChangeSnapshot("repo", "+change"), "rules",
            new ModelSettings { Model = "test", AllowHttp = true }, "", default);
        Assert.Contains("截断", result.FallbackReason);
        Assert.DoesNotContain("partial", result.Text);
    }

    [Fact]
    public async Task LargeGitDiffFallsBackOnLaterBatchFailureAndPreservesCancellation()
    {
        var settings = new ModelSettings { Model = "local", AllowHttp = true, MaxInputCharacters = 500 };
        var snapshot = new ChangeSnapshot("repo", string.Concat(Enumerable.Repeat("+changed content\n", 160)),
            files: new[] { new ChangeFile("file.cpp", "修改") });
        var handler = new Handler { StatusForCall = call => call == 2 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK };
        using var client = new HttpClient(handler);
        var generator = new CommitGenerator(client);
        var local = await generator.GenerateAsync(snapshot, "rules", settings, "", default);
        Assert.Equal(2, local.Requests);
        Assert.Contains("429", local.FallbackReason);
        Assert.Contains("本地摘要", local.Text);
        Assert.Contains("file.cpp", local.Text);
        Assert.Null(local.InputTokens);

        using var cancellation = new CancellationTokenSource();
        handler.StatusForCall = null;
        handler.OnCall = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(snapshot, "rules", settings, "", cancellation.Token));
        Assert.Equal(3, handler.Calls);
    }

    private static JObject ReadPayload(string body, int budget)
    {
        var messages = (JArray)JObject.Parse(body)["messages"]!;
        var system = (string)messages[0]["content"]!;
        var user = (string)messages[1]["content"]!;
        Assert.True(system.Length + user.Length <= budget);
        return JObject.Parse(user);
    }
    [Fact]
    public async Task GitReadsIndexRatherThanUnstagedWorkingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "AutoCommit-test-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try {
            Git(root, "init");
            var file = Path.Combine(root, "中文 文件.txt");
            await File.WriteAllTextAsync(file, "staged_marker\n"); Git(root, "add .");
            await File.WriteAllTextAsync(file, "unstaged_marker\n");
            var snapshot = await new GitChangeProvider().ReadAsync(root, default);
            Assert.Contains("staged_marker", snapshot.Diff); Assert.DoesNotContain("unstaged_marker", snapshot.Diff);
            Assert.Single(snapshot.Files);
            Assert.Equal("中文 文件.txt", snapshot.Files[0].Path);
            Git(root, "add .");
            var changed = await new GitChangeProvider().ReadAsync(root, default);
            Assert.NotEqual(snapshot.Fingerprint, changed.Fingerprint);
            var provider = new GitChangeProvider();
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CommitAsync(snapshot, "过期提交", default));
            Git(root, "config user.name AutoCommitTest");
            Git(root, "config user.email test@example.invalid");
            Git(root, "config commit.gpgsign false");
            Git(root, "config core.hooksPath .git/disabled-test-hooks");
            await provider.CommitAsync(changed, "feat(test): 中文提交\n\n- 验证 UTF-8 消息", default);
            Assert.Empty((await provider.ReadAsync(root, default)).Diff);
            var log = Git(root, "log -1 --format=%B");
            Assert.Contains("feat(test): 中文提交", log);
            Assert.Contains("验证 UTF-8 消息", log);
            // A real commit must contain only the staged version, not later working tree edits.
            await File.WriteAllTextAsync(file, "second_staged\n"); Git(root, "add .");
            await File.WriteAllTextAsync(file, "second_unstaged\n");
            var partial = await provider.ReadAsync(root, default);
            await provider.CommitAsync(partial, "fix: 保留未暂存修改", default);
            Assert.Contains("second_unstaged", await File.ReadAllTextAsync(file));
            Assert.Contains("second_staged", Git(root, "show HEAD:\"中文 文件.txt\""));

            // Capturing a patch for batching must preserve data after the preview boundary.
            await File.WriteAllTextAsync(file, new string('x', 2_100_000) + "\nLARGE_STAGED_TAIL\n");
            Git(root, "add .");
            await File.WriteAllTextAsync(file, "LARGE_UNSTAGED_TAIL\n");
            using var capture = new StringWriter();
            var large = await provider.ReadAsync(root, default, capture);
            Assert.True(large.DiffIsPartial);
            Assert.DoesNotContain("LARGE_STAGED_TAIL", large.Diff);
            Assert.Contains("LARGE_STAGED_TAIL", capture.ToString());
            Assert.DoesNotContain("LARGE_UNSTAGED_TAIL", capture.ToString());
            Assert.Equal(large.Fingerprint, (await provider.ReadAsync(root, default)).Fingerprint);
        } finally {
            var full = Path.GetFullPath(root);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), full);
            Assert.StartsWith("AutoCommit-test-", Path.GetFileName(full));
            foreach (var path in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(full, true);
        }
    }
    private static string Git(string root, string args)
    {
        using var p = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(); Assert.Equal(0, p.ExitCode); return output;
    }
}
