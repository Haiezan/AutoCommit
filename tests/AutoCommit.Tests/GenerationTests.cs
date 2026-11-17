using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoCommit.Core;
using Xunit;

public class GenerationTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public string Body = "";
        public string Url = "";
        public string Reply = "{\"choices\":[{\"message\":{\"content\":\"fix: 修复错误\"},\"finish_reason\":\"stop\"}]}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Body = await request.Content!.ReadAsStringAsync(); Url = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply) };
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
    [Theory]
    [InlineData("", 48000, true)]
    [InlineData("+change", 1, true)]
    [InlineData("+change", 48000, false)]
    public async Task InvalidInputDoesNotSendCode(string diff, int budget, bool allowHttp)
    {
        var handler = new Handler(); using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CommitGenerator(client).GenerateAsync(new ChangeSnapshot("repo", diff), "rules",
            new ModelSettings { Model = "test", AllowHttp = allowHttp, MaxInputCharacters = budget }, "", default));
        Assert.Equal(0, handler.Calls);
    }
    [Fact]
    public async Task RejectsTruncatedOutput()
    {
        var handler = new Handler { Reply = "{\"choices\":[{\"message\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}" };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CommitGenerator(client).GenerateAsync(new ChangeSnapshot("repo", "+change"), "rules",
            new ModelSettings { Model = "test", AllowHttp = true }, "", default));
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
