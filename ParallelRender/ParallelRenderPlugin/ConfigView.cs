using System.Windows;
using System.Windows.Controls;

namespace ParallelRender;

internal sealed class ConfigView : UserControl
{
    public ConfigView(ParallelSettings s)
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        UIElement Row(string label, Control c)
        {
            var g = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            g.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
            g.Children.Add(c);
            return g;
        }
        ComboBox Combo(string[] items, int sel, Action<int> set)
        {
            var c = new ComboBox { ItemsSource = items, SelectedIndex = sel };
            c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) { set(c.SelectedIndex); s.Save(); } };
            return c;
        }
        TextBox Num(int v, Action<int> set, int min, int max)
        {
            var t = new TextBox { Text = v.ToString() };
            t.LostFocus += (_, _) =>
            {
                if (int.TryParse(t.Text, out var n)) { n = Math.Clamp(n, min, max); set(n); t.Text = n.ToString(); s.Save(); }
                else t.Text = v.ToString();
            };
            return t;
        }

        var enabled = new CheckBox { Content = "並列描画を使う (OFF なら同じエンコーダーで逐次出力)", IsChecked = s.Enabled, Margin = new Thickness(0, 0, 0, 10) };
        enabled.Checked += (_, _) => { s.Enabled = true; s.Save(); };
        enabled.Unchecked += (_, _) => { s.Enabled = false; s.Save(); };
        panel.Children.Add(enabled);
        panel.Children.Add(Row($"並列度 (0 = 自動: min(4, 論理CPU数) = {Math.Min(4, Environment.ProcessorCount)})", Num(s.Parallelism, n => s.Parallelism = n, 0, 32)));
        panel.Children.Add(Row("チャンク長 (フレーム)", Num(s.ChunkFrames, n => s.ChunkFrames = n, 1, 600)));
        panel.Children.Add(Row("順序再構成バッファの上限 (MB)", Num(s.BufferMb, n => s.BufferMb = n, 64, 16384)));
        panel.Children.Add(Row("GPU 直渡し (ワーカーが共有テクスチャへ直接描き、CPU 読み戻しを省く。使えなければ自動で CPU 読み戻し)", Combo(["自動", "OFF", "ON"], (int)s.GpuDirect, i => s.GpuDirect = (GpuDirectMode)i)));
        var adapt = new CheckBox { Content = "先行フレーム数の上限を自動で調整する (エンコード待ちが多く VRAM とメモリに余裕があれば広げ、不足すれば縮める)", IsChecked = s.AdaptiveWindow, Margin = new Thickness(0, 0, 0, 10) };
        adapt.Checked += (_, _) => { s.AdaptiveWindow = true; s.Save(); };
        adapt.Unchecked += (_, _) => { s.AdaptiveWindow = false; s.Save(); };
        panel.Children.Add(adapt);
        panel.Children.Add(Row("コーデック", Combo(["H.264", "H.265 (HEVC)", "AV1"], (int)s.Codec, i => s.Codec = (NvencCodec)i)));
        panel.Children.Add(Row("品質", Combo(["速度優先", "バランス", "品質優先"], (int)s.Quality, i => s.Quality = (NvencQuality)i)));
        panel.Children.Add(Row("レート制御", Combo(["固定", "可変", "YouTube 推奨"], (int)s.RateControl, i => s.RateControl = (NvencRateControl)i)));
        panel.Children.Add(Row("ビットレート (kbps、YouTube 推奨以外)", Num(s.BitrateKbps, n => s.BitrateKbps = n, 100, 200000)));
        Content = panel;
    }
}
