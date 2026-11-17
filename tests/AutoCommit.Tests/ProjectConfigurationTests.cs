using System;
using System.IO;
using AutoCommit.Core;
using Xunit;

public class ProjectConfigurationTests
{
    [Fact]
    public void ReadingMissingProjectConfigurationKeepsDirectoryUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "AutoCommit-config-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try {
            var defaults = new ModelSettings { Model = "local-default" };
            Assert.Same(defaults, ProjectConfiguration.LoadOrDefault(root, defaults));
            Assert.False(Directory.Exists(ProjectConfiguration.Folder(root)));
        } finally { Directory.Delete(root); }
    }

    [Fact]
    public void InitializationMigratesRulesAndPreservesExistingProjectEdits()
    {
        var root = Path.Combine(Path.GetTempPath(), "AutoCommit-config-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try {
            File.WriteAllText(Path.Combine(root, ".commit-rules.md"), "原项目规范");
            ProjectConfiguration.Initialize(root, new ModelSettings { Model = "initial" });
            Assert.Equal("原项目规范", File.ReadAllText(ProjectConfiguration.RulesPath(root)));
            ProjectConfiguration.Save(root, new ModelSettings { Model = "project", MaxInputCharacters = 12000 });
            File.WriteAllText(ProjectConfiguration.RulesPath(root), "自定义规范");
            ProjectConfiguration.Initialize(root, new ModelSettings { Model = "other" });
            Assert.Equal("project", ProjectConfiguration.Load(root).Model);
            Assert.Equal(12000, ProjectConfiguration.Load(root).MaxInputCharacters);
            Assert.Equal("自定义规范", File.ReadAllText(ProjectConfiguration.RulesPath(root)));
            Assert.DoesNotContain("ApiKey", File.ReadAllText(ProjectConfiguration.ModelPath(root)), StringComparison.OrdinalIgnoreCase);
        } finally {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            Assert.StartsWith("AutoCommit-config-", Path.GetFileName(root));
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void InitializationWithoutLegacyRulesUsesCompleteDefault()
    {
        var root = Path.Combine(Path.GetTempPath(), "AutoCommit-config-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try {
            ProjectConfiguration.Initialize(root, new ModelSettings());
            Assert.Equal(DefaultRules.Text, File.ReadAllText(ProjectConfiguration.RulesPath(root)));
            Assert.True(File.Exists(Path.Combine(ProjectConfiguration.Folder(root), ".gitignore")));
        } finally {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
            Assert.StartsWith("AutoCommit-config-", Path.GetFileName(root));
            Directory.Delete(root, true);
        }
    }
}
