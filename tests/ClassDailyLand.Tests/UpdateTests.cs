using System.IO;
using ClassDailyLand.Infrastructure.Updates;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>源码运行保护（对应 dev_guard.py 的 scan_source_entry）。</summary>
public class SourceTreeGuardTests : IDisposable
{
    private readonly string _root;

    public SourceTreeGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cdl-guard", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);

        return full;
    }

    [Fact]
    public void 空目录不算源码树()
    {
        Assert.False(SourceTreeGuard.IsSourceTree(_root));
        Assert.Empty(SourceTreeGuard.Scan(_root));
    }

    [Fact]
    public void 目录不存在不算源码树()
        => Assert.False(SourceTreeGuard.IsSourceTree(Path.Combine(_root, "不存在")));

    [Theory]
    [InlineData("ClassDailyLand.cs")]
    [InlineData("Class Daily Land.cs")]
    [InlineData("class_daily_land.cs")]
    [InlineData("ClassDailyLand.py")]
    [InlineData("Program.cs")]
    [InlineData("Main.cs")]
    public void 主入口文件名命中(string fileName)
    {
        Write(fileName, "// 空文件也命中，因为文件名本身就是特征");

        var hits = SourceTreeGuard.Scan(_root);

        Assert.Contains(hits, h => h.How == "name");
    }

    [Fact]
    public void 工程文件是强信号()
    {
        Write("App.sln", "");
        Write("src/App.csproj", "<Project />");

        var hits = SourceTreeGuard.Scan(_root);

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.Equal("project", h.How));
    }

    [Fact]
    public void 内容特征命中()
    {
        Write("Entry.cs", """
        using ClassDailyLand.App.Services;

        internal static class Launcher
        {
            [STAThread]
            private static void Main()
            {
                var app = new App();
                app.Run();
            }
        }
        """);

        var hits = SourceTreeGuard.Scan(_root);

        Assert.Contains(hits, h => h.How == "pattern");
    }

    [Fact]
    public void 只有Main没有核心类型引用不算命中()
    {
        // 一个不相干的小工具也有 Main，不该被当成主入口
        Write("Tool.cs", """
        internal static class Tool
        {
            private static void Main() { System.Console.WriteLine("hi"); }
        }
        """);

        Assert.Empty(SourceTreeGuard.Scan(_root));
    }

    [Fact]
    public void 只有核心类型引用没有主入口不算命中()
    {
        Write("Helper.cs", """
        using ClassDailyLand.Core.Models;

        internal static class Helper
        {
            public static string Describe(AppSettings settings) => settings.ThemeMode;
        }
        """);

        Assert.Empty(SourceTreeGuard.Scan(_root));
    }

    [Fact]
    public void 注释里的特征字样不算命中()
    {
        // 这是源实现特意处理过的坑：保护模块自己写在注释里的字样
        // 也会被算成命中，命中列表里就会多出无关文件
        Write("Notes.cs", """
        // 说明：正式入口在 ClassDailyLand.App 里，static void Main 也在那边
        // using ClassDailyLand.Core;

        public sealed class Notes
        {
        }
        """);

        Assert.Empty(SourceTreeGuard.Scan(_root));
    }

    [Fact]
    public void 扫描器自身不会命中()
    {
        Write("SourceTreeGuard.cs", """
        using ClassDailyLand.Core.Abstractions;

        public static class SourceTreeGuard
        {
            public static void Main() { }
        }
        """);

        Assert.Empty(SourceTreeGuard.Scan(_root));
    }

    [Fact]
    public void 数据目录与构建产物被跳过()
    {
        Write("settings/ClassDailyLand.cs", "");
        Write("plugins/Main.cs", "");
        Write("obj/App.csproj", "");
        Write("bin/Program.cs", "");
        Write("_internal/Main.cs", "");
        Write(".git/Main.cs", "");
        Write("publish/App.sln", "");

        Assert.Empty(SourceTreeGuard.Scan(_root));
    }

    [Fact]
    public void 隐藏目录被跳过()
    {
        Write(".hidden/Main.cs", "");

        Assert.Empty(SourceTreeGuard.Scan(_root));
    }

    [Fact]
    public void 只扫描两层目录()
    {
        Write("a/Main.cs", "");              // 第一层 → 命中
        Write("a/b/Main.cs", "");            // 第二层 → 命中
        Write("a/b/c/Main.cs", "");          // 第三层 → 不扫

        var hits = SourceTreeGuard.Scan(_root);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void 非源码文件不参与判定()
    {
        Write("readme.md", "static void Main() ClassDailyLand.App");
        Write("data.json", "{}");

        Assert.Empty(SourceTreeGuard.Scan(_root));
    }

    [Fact]
    public void 命中文件名摘要会长截断()
    {
        Write("Program.cs", "");
        Write("App.cs", "");
        Write("Main.cs", "");
        Write("Start.cs", "");

        var brief = SourceTreeGuard.BriefNames(SourceTreeGuard.Scan(_root));

        Assert.Contains("等 4 个文件", brief);
    }
}

/// <summary>更新器定位与退出信号（对应 launch_updater.py）。</summary>
public class UpdateLauncherTests
{
    [Fact]
    public void 退出标志路径落在临时目录下的约定位置()
    {
        var path = UpdateLauncher.GetQuitFlagPath();

        Assert.StartsWith(Path.GetTempPath(), path);
        Assert.EndsWith(Path.Combine("ClassDailyLandLauncher", "quit.flag"), path);
    }

    [Fact]
    public void 清理退出标志不会抛异常()
    {
        UpdateLauncher.ClearQuitFlag();
        UpdateLauncher.ClearQuitFlag();      // 重复调用同样安全
    }

    [Fact]
    public void 没有更新器时报出可读原因()
    {
        using var workspace = new TestWorkspace();
        var settings = new ClassDailyLand.Infrastructure.Settings.SettingsService(workspace.Store);

        // 指向一个空目录，既没有 Launcher.exe 也没有 launcher.py
        var isolated = Path.Combine(workspace.Root, "app");
        Directory.CreateDirectory(isolated);

        var launcher = new UpdateLauncher(workspace.Paths, settings, isolated);

        Assert.False(launcher.IsUpdaterAvailable);
        Assert.Null(launcher.LauncherPath);

        var result = launcher.LaunchVisible(force: true);

        Assert.False(result.Success);
        Assert.False(result.Spawned);
        Assert.Contains("Launcher.exe", result.Message);
    }

    [Fact]
    public void 源码保护开启时命中主入口会拦下()
    {
        using var workspace = new TestWorkspace();
        var settings = new ClassDailyLand.Infrastructure.Settings.SettingsService(workspace.Store);

        var appDirectory = Path.Combine(workspace.Root, "app");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "ClassDailyLand.cs"), "");

        var launcher = new UpdateLauncher(workspace.Paths, settings, appDirectory);
        var (allowed, hits) = launcher.EvaluateSourceGuard();

        Assert.False(allowed);
        Assert.Single(hits);
    }

    [Fact]
    public void 源码保护关闭时不再拦截()
    {
        using var workspace = new TestWorkspace();
        var settings = new ClassDailyLand.Infrastructure.Settings.SettingsService(workspace.Store);

        var appDirectory = Path.Combine(workspace.Root, "app");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "ClassDailyLand.cs"), "");

        settings.Update(s => s.BlockUpdateOnSource = false);

        var launcher = new UpdateLauncher(workspace.Paths, settings, appDirectory);
        var (allowed, hits) = launcher.EvaluateSourceGuard();

        Assert.True(allowed);
        Assert.Empty(hits);
    }

    [Fact]
    public void 静默检查命中源码时不拉起更新器()
    {
        using var workspace = new TestWorkspace();
        var settings = new ClassDailyLand.Infrastructure.Settings.SettingsService(workspace.Store);

        var appDirectory = Path.Combine(workspace.Root, "app");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "Main.cs"), "");

        var launcher = new UpdateLauncher(workspace.Paths, settings, appDirectory);
        var result = launcher.LaunchHidden();

        Assert.True(result.Success);         // 被拦下不算失败
        Assert.False(result.Spawned);
        Assert.NotNull(result.Hits);
        Assert.NotEmpty(result.Hits!);
    }

    [Fact]
    public void 手动检查命中源码但用户取消时不拉起()
    {
        using var workspace = new TestWorkspace();
        var settings = new ClassDailyLand.Infrastructure.Settings.SettingsService(workspace.Store);

        var appDirectory = Path.Combine(workspace.Root, "app");
        Directory.CreateDirectory(appDirectory);
        File.WriteAllText(Path.Combine(appDirectory, "Main.cs"), "");

        var launcher = new UpdateLauncher(workspace.Paths, settings, appDirectory);
        var asked = false;

        var result = launcher.LaunchVisible(hits =>
        {
            asked = true;
            return false;      // 用户点了「取消，不更新」
        });

        Assert.True(asked);
        Assert.True(result.Success);
        Assert.False(result.Spawned);
    }

    [Fact]
    public void 备份提示包含命中文件名()
    {
        var hits = new[] { new SourceEntryHit(@"C:\x\ClassDailyLand.cs", "name") };
        var tip = UpdateLauncher.BackupTip(hits);

        Assert.Contains("备份", tip);
        Assert.Contains("ClassDailyLand.cs", tip);
    }
}
