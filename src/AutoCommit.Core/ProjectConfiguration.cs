using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace AutoCommit.Core;

public static class ProjectConfiguration
{
    public static string Folder(string root) => Path.Combine(root, ".auto");
    public static string RulesPath(string root) => Path.Combine(Folder(root), "commit-rules.md");
    public static string ModelPath(string root) => Path.Combine(Folder(root), "model.json");

    public static void Initialize(string root, ModelSettings defaults)
    {
        Directory.CreateDirectory(Folder(root));
        WriteIfMissing(ModelPath(root), JsonConvert.SerializeObject(defaults, Formatting.Indented));
        var legacy = Path.Combine(root, ".commit-rules.md");
        if (!File.Exists(RulesPath(root))) {
            if (File.Exists(legacy) && new FileInfo(legacy).Length > 65536)
                throw new InvalidOperationException("旧提交规范超过 64KB，请缩小文件后重试。");
            WriteIfMissing(RulesPath(root), File.Exists(legacy) ? File.ReadAllText(legacy) : DefaultRules.Text);
        }
        WriteIfMissing(Path.Combine(Folder(root), ".gitignore"), "# Local secrets must not be shared\n*.local.*\n*.key\n*.dat\n");
        WriteIfMissing(Path.Combine(Folder(root), "README.md"),
            "# AutoCommit 项目配置\n\n- model.json：模型地址、名称、HTTP 开关和输入字符预算，可通过插件设置保存。不要在此填写 API Key。\n" +
            "- commit-rules.md：项目提交规范，可编辑并纳入版本控制。\n" +
            "- API Key：在插件设置中填写，按 Windows 用户加密保存在本机，不写入项目。\n" +
            "- 切换电脑或用户后需重新填写 Key；更换服务地址后也需重新填写。\n" +
            "- 支持 Git 和 TFVC；TFVC 需在 Visual Studio 的 Team Explorer 中连接并映射工作区。\n");
    }

    public static ModelSettings Load(string root)
    {
        var path = ModelPath(root);
        if (new FileInfo(path).Length > 65536) throw new InvalidOperationException("模型配置超过 64KB。");
        return JsonConvert.DeserializeObject<ModelSettings>(File.ReadAllText(path))
            ?? throw new InvalidOperationException(".auto/model.json 不是有效的模型配置。");
    }

    public static ModelSettings LoadOrDefault(string root, ModelSettings defaults) =>
        File.Exists(ModelPath(root)) ? Load(root) : defaults;

    public static void Save(string root, ModelSettings settings)
    {
        Directory.CreateDirectory(Folder(root));
        File.WriteAllText(ModelPath(root), JsonConvert.SerializeObject(settings, Formatting.Indented), new UTF8Encoding(false));
    }

    private static void WriteIfMissing(string path, string content)
    {
        if (File.Exists(path)) return;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(content);
    }
}
