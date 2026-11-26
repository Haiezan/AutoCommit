using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
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
        Assert.Null(JObject.Parse(handler.Body)["enable_thinking"]);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedGbkAndUtf8ChangesReachModel(bool tfvc)
    {
        // The production VS host is .NET Framework. The .NET 8 test host opts into Windows code pages.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var root = Directory.CreateTempSubdirectory("AutoCommit-encoding-").FullName;
        try {
            if (!tfvc) Git(root, "init");
            var changes = new List<TfvcFileChange>();
            foreach (var (name, codePage, marker) in new[] { ("legacy.cpp", 936, "步长"), ("modern.cpp", 65001, "输出") }) {
                var path = Path.Combine(root, name);
                var encoding = Encoding.GetEncoding(codePage);
                File.WriteAllBytes(path, encoding.GetBytes("// 旧" + marker + "\n"));
                var before = TfvcContent.ReadFile(path, true, codePage);
                File.WriteAllBytes(path, encoding.GetBytes("// 新" + marker + "\n"));
                if (tfvc) changes.Add(TfvcContent.CreateChange("$/" + name, "Edit", null, before,
                    TfvcContent.ReadFile(path, true, codePage), 10000, default));
            }

            ChangeSnapshot snapshot;
            if (tfvc) snapshot = new ChangeSnapshot(root, "", tfvcChanges: changes);
            else {
                Git(root, "add .");
                await File.WriteAllTextAsync(Path.Combine(root, "legacy.cpp"), "UNSTAGED");
                using var capture = new StringWriter();
                snapshot = await new GitChangeProvider().ReadAsync(root, default, capture);
                Assert.Equal(snapshot.Diff, capture.ToString());
            }

            var handler = new Handler();
            using var client = new HttpClient(handler);
            var settings = new ModelSettings { BaseUrl = "https://example.test/v1", Model = "local" };
            await new CommitGenerator(client).GenerateAsync(snapshot, "rules", settings, "", default);
            var diff = (string)ReadPayload(Assert.Single(handler.Bodies), settings.MaxInputCharacters)["stagedDiff"]!;
            Assert.Contains("+// 新步长", diff);
            Assert.Contains("+// 新输出", diff);
            Assert.DoesNotContain("UNSTAGED", diff);
            if (tfvc) {
                Assert.Contains("-// 旧步长", diff);
                Assert.Contains("-// 旧输出", diff);
            }
        } finally {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
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
        using var capture = new StringReader("文件：$/large.cpp\n@@ -1,1 +1,1 @@\n+" + new string('"', 2400) +
            "🚀\n+NOT_IN_PREVIEW\n文件：$/binary；文件超过 1 MiB\n");
        Assert.Contains("分批", CommitGenerator.PreviewDiff(snapshot, rules, settings.MaxInputCharacters));
        var result = await generator.GenerateAsync(snapshot, rules, settings, "", default, capture);
        var payloads = handler.Bodies.Select(body => ReadPayload(body, settings.MaxInputCharacters)).ToArray();
        var leaves = payloads.Select(payload => (string?)payload["stagedDiff"]).Where(diff => diff != null).ToArray();
        Assert.True(leaves.Length >= 4);
        Assert.Contains(leaves, diff => diff!.Contains("NOT_IN_PREVIEW"));
        Assert.Contains(leaves, diff => diff!.Contains("🚀"));
        Assert.Contains(leaves, diff => diff!.Contains("$/binary"));
        Assert.Equal(2400, leaves.Sum(diff => diff!.Count(c => c == '"')));
        Assert.Contains(payloads.Take(payloads.Length - 1), payload => payload["changeSummaries"] != null);
        Assert.Equal(rules, (string?)payloads[^1]["commitRules"]);
        Assert.Equal("变更事实", result.Text);
        Assert.Null(result.FallbackReason);
        Assert.Equal(handler.Calls, result.Requests);
        Assert.Equal(handler.Calls * 12, result.InputTokens);
        Assert.Null(result.TotalTokens);
        var final = JObject.Parse(handler.Body)["messages"]!;
        var direct = new ChangeSnapshot("repo", "+示例");
        await generator.GenerateAsync(direct, rules, settings, "", default);
        var directBody = handler.Body;
        await generator.GenerateAsync(direct, rules, settings, "", default);
        Assert.Equal(directBody, handler.Body);
        var messages = JObject.Parse(directBody)["messages"]!;
        Assert.True(JToken.DeepEquals(messages[0], final[0]));
        var prefix = "{\"commitRules\":" + Newtonsoft.Json.JsonConvert.SerializeObject(rules) + ",";
        Assert.StartsWith(prefix, (string)messages[1]!["content"]!);
        Assert.StartsWith(prefix, (string)final[1]!["content"]!);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutSplitsDiffAndKeepsCompletedFacts(bool direct)
    {
        var lines = Enumerable.Range(0, 200).Select(index => $"+marker-{index} sample change with additional text\n").ToArray();
        var diff = "文件：$/sample\n@@ -1,1 +1,200 @@\n" + string.Concat(lines);
        var snapshot = direct ? new ChangeSnapshot("repo", diff) : new ChangeSnapshot("repo", "",
            tfvcChanges: new[] { new TfvcFileChange("文件：$/sample", hasOmittedHunks: true) });
        using var capture = direct ? null : new StringReader(diff);
        var handler = new Handler {
            Reply = "{\"choices\":[{\"message\":{\"content\":\"变更事实\"}}],\"usage\":{\"prompt_tokens\":12}}",
            StatusForCall = call => !direct && call == 2 ? HttpStatusCode.RequestTimeout : HttpStatusCode.OK,
            OnCall = call => { if (direct && call == 1) throw new TaskCanceledException(); }
        };
        using var client = new HttpClient(handler);
        var settings = new ModelSettings {
            BaseUrl = "https://example.test/v1", Model = "model", MaxInputCharacters = direct ? 48000 : 4300, EnableThinking = false
        };
        var result = await new CommitGenerator(client).GenerateAsync(snapshot, "项目规范", settings, "", default, capture);
        Assert.Null(result.FallbackReason);
        Assert.Equal(handler.Calls, result.Requests);
        Assert.Null(result.InputTokens);
        Assert.All(handler.Bodies, body => Assert.False((bool)JObject.Parse(body)["enable_thinking"]!));
        var payloads = handler.Bodies.Select(body => ReadPayload(body, settings.MaxInputCharacters)).ToArray();
        var successfulDiffs = payloads.Where((_, index) => index != (direct ? 0 : 1))
            .Select(payload => (string?)payload["stagedDiff"]).Where(payload => payload != null).ToArray();
        Assert.True(successfulDiffs.Length > 2);
        var successfulLines = successfulDiffs.SelectMany(part => Regex.Matches(part!, @"(?m)^\+marker-.*\n")
            .Cast<Match>().Select(match => match.Value));
        Assert.Equal(lines, successfulLines);
        Assert.Equal("项目规范", (string?)payloads[^1]["commitRules"]);
        Assert.Contains("变更事实", (string?)payloads[^1]["changeSummaries"]);
    }

    [Fact]
    public async Task PersistentTimeoutStopsAfterTwoSplitsAndReportsBatch()
    {
        var diff = string.Concat(Enumerable.Repeat("+sample line with sufficient text\n", 2000));
        var handler = new Handler { StatusForCall = _ => HttpStatusCode.GatewayTimeout };
        using var client = new HttpClient(handler);
        var settings = new ModelSettings { Model = "local", AllowHttp = true, MaxInputCharacters = 8000 };
        var result = await new CommitGenerator(client).GenerateAsync(new ChangeSnapshot("repo", diff), "规则", settings, "", default);
        Assert.Equal(3, result.Requests);
        Assert.Matches(@"汇总第 1\.1\.1 批变更.*模型请求超时.*HTTP 504", result.FallbackReason!);
        Assert.Contains("本地摘要", result.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetriesTimedOutCompositionWithoutRestarting(bool batched)
    {
        var handler = new Handler();
        var failures = 0;
        handler.StatusForCall = _ => ReadPayload(handler.Body, 8000)["commitRules"] != null && failures++ == 0
            ? HttpStatusCode.GatewayTimeout : HttpStatusCode.OK;
        using var client = new HttpClient(handler);
        var settings = new ModelSettings { Model = "local", AllowHttp = true, MaxInputCharacters = 8000 };
        var diff = batched ? new string('x', 16000) : "+change";
        var result = await new CommitGenerator(client).GenerateAsync(new ChangeSnapshot("repo", diff), "规则", settings, "", default);
        Assert.Null(result.FallbackReason);
        Assert.Equal(2, failures);
        Assert.Equal(handler.Bodies[^2], handler.Bodies[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalRequestUsesFileFactsAndFallbackRetainsThem(bool failFinal)
    {
        var names = Enumerable.Range(0, 8).Select(i => $"$/module{i}/code.cs").ToArray();
        var files = names.Select(path => new ChangeFile(path, "修改")).ToArray();
        var snapshot = new ChangeSnapshot("repo", "", files: files,
            tfvcChanges: new[] { new TfvcFileChange("文件：" + names[0], hasOmittedHunks: true) });
        using var capture = new StringReader(string.Concat(names.Select(path =>
            $"文件：{path}；操作：修改；模型输入：完整差异\n+" + new string('X', 400) + "\n")));
        var handler = new Handler();
        var logs = new List<string>();
        string? final = null;
        handler.StatusForCall = _ => {
            var payload = ReadPayload(handler.Body, 2400);
            if (payload["commitRules"] != null) {
                final = (string?)payload["changeSummaries"];
                return failFinal ? HttpStatusCode.BadRequest : HttpStatusCode.OK;
            }
            Assert.Null(payload["changeSummaries"]);
            var ids = Regex.Matches((string)payload["stagedDiff"]!, @"(?m)^\[F\d+\]$").Cast<Match>();
            var facts = string.Join("\n", ids.Select(m => m.Value + " 模块事实"));
            handler.Reply = Newtonsoft.Json.JsonConvert.SerializeObject(new { choices = new[] { new { message = new { content = facts } } } });
            return HttpStatusCode.OK;
        };
        using var client = new HttpClient(handler);
        var settings = new ModelSettings { Model = "local", AllowHttp = true, MaxInputCharacters = 2400 };
        var result = await new CommitGenerator(client).GenerateAsync(snapshot, "项目规范", settings, "secret-marker", default, capture, log: logs.Add);
        Assert.NotNull(final);
        foreach (var name in names) Assert.Contains(name, final);
        Assert.True(result.Requests < names.Length);
        if (failFinal) {
            Assert.True(result.PartialSummary);
            Assert.Contains("部分摘要", result.Text);
            Assert.Contains("模块事实", result.Text);
        } else Assert.Null(result.FallbackReason);
        var log = string.Join("\n", logs);
        Assert.Contains("超时 0 次", log);
        Assert.DoesNotContain("secret-marker", log);
        Assert.DoesNotContain("模块事实", log);
        Assert.DoesNotContain("$/module", log);
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
    public async Task StageAllOffersOnlyEligibleChangesAndRechecksBeforeWritingIndex()
    {
        var root = Directory.CreateTempSubdirectory("AutoCommit-stage-").FullName;
        try {
            Git(root, "init");
            Git(root, "config user.email test@example.invalid");
            Git(root, "config user.name AutoCommitTest");
            Git(root, "config commit.gpgsign false");
            Git(root, "config core.hooksPath .git/disabled-test-hooks");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "ignored.txt\n");
            var tracked = Path.Combine(root, "tracked.txt");
            var deleted = Path.Combine(root, "deleted.txt");
            File.WriteAllText(tracked, "original");
            File.WriteAllText(deleted, "original");
            Git(root, "add .");
            Git(root, "commit -m initial");
            var head = Git(root, "rev-parse HEAD");
            File.WriteAllText(Path.Combine(root, "ignored.txt"), "ignored");
            var provider = new GitChangeProvider();
            Assert.False((await provider.ReadAsync(root, default)).CanStageAll);

            File.WriteAllText(Path.Combine(root, "new.txt"), "new");
            var untracked = await provider.ReadAsync(root, default);
            Assert.True(untracked.CanStageAll);
            Assert.Empty(untracked.Files);
            File.WriteAllText(tracked, "modified");
            File.Delete(deleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StageAllAsync(untracked, default));
            Assert.Empty(Git(root, "diff --cached --name-only"));

            var emptyIndex = await provider.ReadAsync(root, default);
            Git(root, "add tracked.txt");
            Assert.False((await provider.ReadAsync(root, default)).CanStageAll);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.StageAllAsync(emptyIndex, default));
            Assert.Equal("tracked.txt", Git(root, "diff --cached --name-only").Trim());

            Git(root, "reset --quiet HEAD");
            var staged = await provider.StageAllAsync(await provider.ReadAsync(root, default), default);
            Assert.Equal(new[] { "deleted.txt", "new.txt", "tracked.txt" }, staged.Files.Select(f => f.Path));
            Assert.False(staged.CanStageAll);
            Assert.Equal(head, Git(root, "rev-parse HEAD"));
        } finally {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
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
