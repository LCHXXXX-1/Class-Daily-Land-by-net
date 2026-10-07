using ClassDailyLand.Infrastructure.Settings;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>
/// 设置服务测试。核心验证「逐字段容错回退」，对应源项目 settings_manager._valid_type。
/// </summary>
public sealed class SettingsServiceTests
{
    [Fact]
    public void 首次运行_文件缺失时写入默认配置()
    {
        using var workspace = new TestWorkspace();
        var service = new SettingsService(workspace.Store);

        service.Load();

        Assert.True(File.Exists(workspace.ConfigFile("settings.json")));
        Assert.Equal("system", service.Current.ThemeMode);
        Assert.Equal(0.25, service.Current.MainWidthRatio);
        Assert.Equal(300, service.Current.IslandAlertWidth);
        Assert.True(service.Current.ShowIsland);
        Assert.Equal(14, service.Current.MainFontSize);
    }

    [Fact]
    public void 单个字段类型错误_只回退该字段其余照常生效()
    {
        using var workspace = new TestWorkspace();

        // main_font_size 被写成了字符串（人为误编辑），应当只让这个字段回退默认值
        workspace.WriteConfigRaw("settings.json", """
        {
          "theme_mode": "dark",
          "main_font_size": "not-a-number",
          "island_top_margin": "8",
          "show_island": false,
          "main_opacity": 0.5
        }
        """);

        var service = new SettingsService(workspace.Store);
        service.Load();

        // 合法字段全部生效
        Assert.Equal("dark", service.Current.ThemeMode);
        Assert.False(service.Current.ShowIsland);
        Assert.Equal(0.5, service.Current.MainOpacity);

        // 非法字段回退默认值
        Assert.Equal(14, service.Current.MainFontSize);

        // 未出现在文件中的字段保持默认（这里是布尔字段，同样不因文件缺失而变化）
        Assert.True(service.Current.AnimFadeWindow);
    }

    [Fact]
    public void 数值字段宽松互通_整数与浮点皆可()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("settings.json", """{"main_font_size": 16.0, "main_opacity": 1}""");

        var service = new SettingsService(workspace.Store);
        service.Load();

        // 对应源项目 isinstance(v, (int, float)) 的宽松判定
        Assert.Equal(16, service.Current.MainFontSize);
        Assert.Equal(1.0, service.Current.MainOpacity);
    }

    [Fact]
    public void 更新会落盘并触发变更事件()
    {
        using var workspace = new TestWorkspace();
        var service = new SettingsService(workspace.Store);
        service.Load();

        var raised = 0;
        service.Changed += (_, _) => raised++;

        service.Update(settings => settings.ShowIsland = false);

        Assert.Equal(1, raised);
        Assert.Contains("\"show_island\": false", workspace.ReadConfigRaw("settings.json"));

        // 重新加载后仍能读到修改后的值
        var reloaded = new SettingsService(workspace.Store);
        reloaded.Load();
        Assert.False(reloaded.Current.ShowIsland);
    }

    [Fact]
    public void 配置文件损坏时回退到全默认值()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("settings.json", "这不是 JSON");

        var service = new SettingsService(workspace.Store);
        service.Load();

        Assert.Equal("system", service.Current.ThemeMode);
        Assert.Equal(14, service.Current.MainFontSize);
    }
}
