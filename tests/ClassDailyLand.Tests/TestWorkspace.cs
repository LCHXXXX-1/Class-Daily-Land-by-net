using System.IO;
using ClassDailyLand.Infrastructure.Paths;
using ClassDailyLand.Infrastructure.Storage;

namespace ClassDailyLand.Tests;

/// <summary>
/// 测试夹具：每个测试一份独立的临时数据目录，
/// 保证测试之间零干扰，也保证绝不触碰真实配置。
/// </summary>
internal sealed class TestWorkspace : IDisposable
{
    public string Root { get; }
    public AppPathService Paths { get; }
    public JsonStore Store { get; }

    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "cdl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Paths = new AppPathService(Root);
        Paths.EnsureDirectories();

        Store = new JsonStore(Paths);
    }

    public string ConfigFile(string name) => Path.Combine(Paths.ConfigDirectory, name);

    public void WriteConfigRaw(string name, string json)
        => File.WriteAllText(ConfigFile(name), json);

    public string ReadConfigRaw(string name)
        => File.ReadAllText(ConfigFile(name));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
