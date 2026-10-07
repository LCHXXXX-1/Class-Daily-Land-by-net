using System.Windows;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 用户协议对话框。对应源模块：agreement.py 的 AgreementDialog。
/// 首轮为只读文本 + 同意/退出两按钮，与源项目行为一致。
/// </summary>
public partial class AgreementWindow : Window
{
    /// <summary>协议正文，逐字沿用源项目文本。</summary>
    public const string AgreementText_ = """
欢迎使用“Class Daily Land”软件（以下简称“本软件”）。

在使用本软件前，请您仔细阅读以下条款：

1. 本软件为免费软件，仅供个人学习、班级管理使用。
2. 您承诺不将本软件用于任何非法或违反道德的活动。
3. 本软件作者不对因使用本软件产生的任何数据丢失或损坏负责。
4. 您的使用数据（如值日名单、作业记录）仅保存在本地，不会上传至任何服务器。
5. 软件更新时会自动检查新版本，有新版本时您可自主选择更新。

如您同意以上条款，请点击“同意”继续使用；否则请点击“不同意”退出。

（本协议最终解释权归作者所有）
""";

    public AgreementWindow()
    {
        InitializeComponent();
        AgreementText.Text = AgreementText_.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private void OnAgree(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDecline(object sender, RoutedEventArgs e) => DialogResult = false;
}
