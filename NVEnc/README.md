# NVENC プラグイン出力

YMM4 の出力プラグインです。NVENC のエンコードをパイプライン化して、非同期に行います。このディレクトリは [YMM4_FastExport](../README.md) の一部で、ParallelRender が使う `NvencNative.dll` のソース (`NvencNative/`) と、P/Invoke の宣言 (`NVEncVideoWriterPlugin/NvencNativeMethods.cs`) を含みます。このリポジトリは、単体のプラグインとしての Release は作りません。単体で使うときは、ソースからビルドするか、上流のリリースを使います。

## 由来

[tarutaru247/YMM4_NVEncPlugin](https://github.com/tarutaru247/YMM4_NVEncPlugin) (v1.0、`c7cf161`) を基にした fork です。ライセンスは MIT で、上流の著作権表示を維持しています ([LICENSE](LICENSE))。

Radeon (AMF) 版の [disnana/YMM4_AMF_Plugin](https://github.com/disnana/YMM4_AMF_Plugin) (tp-li-dev / Disnana、MIT) から、ベンダーに依存しない部分と設計を取り込んでいます。参照したコミットは `fb0da45` (v0.2.2) です。

- 所有テクスチャのスロットプールと、専用の出力回収スレッド
- MP4 の音声トラック長 (tkhd) の修正、最初のエラーを保持する処理、入力検証、完了フレーム数の照合
- プロファイリング JSON と、映像・音声を破棄するモード
- 出力検証スクリプト (`scripts/`、`tests/Test-Mp4Timing.ps1`)
- GPU 直渡し (`IVideoFileWriter3`) のオプション

出典と各部品の扱いは、[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) にあります。

この fork の独自の変更は、AV1 のシーケンスヘッダ解析の修正、D3D11 マルチスレッド保護の有効化 (出力スレッドとのデッドロック対策)、実 GPU で動かすネイティブのスモークテスト (`tests/native-smoke/`)、複数のエンコードセッションを 1 本の mp4 にまとめるエクスポート (`NvencEnableOrderedMux` など) です。このエクスポートは、プラグイン本体では使いません。

動作確認は、RTX 4080 での合成フレームによるネイティブ層のテストと、YMM4 4.56.1.0 Lite での 1080p60 H.264 出力までです。HEVC と AV1 の実プロジェクト、長時間の書き出し、複数 GPU は未確認です。

## 必要環境

- Windows 11、NVIDIA GPU と最新のドライバー
- `IVideoFileWriter3` を持つ YMM4 (.NET 10 対応版)
- AV1 を使うときは、AV1 の NVENC に対応した GPU

## 使い方

YMM4 の出力形式から「NVENC プラグイン出力」を選び、コーデック (H.264 / H.265 / AV1)、品質、レート制御を設定します。出力は `.mp4` で、音声は AAC (Media Foundation のエンコーダー) です。H.265 は、既定では非同期にエンコードします。「安定性重視 (遅い)」を選ぶと、同期エンコードになります。

「GPU直渡し (実験・IVideoFileWriter3)」は、既定で OFF です。ON にすると、YMM4 は CPU 読み取り用ビットマップへの中間コピーを省き、描画済みのフレームを GPU 上のまま渡します。プラグイン内の入力テクスチャへの GPU コピーは残ります。速くなるかどうかは未検証です。

「入力テクスチャプール」(4 / 6 / 8、標準 6) は、NVENC に渡す入力テクスチャの枚数です。大きいほど GPU メモリを多く使います。

## GPU の選択

このプラグインは、YMM4 本体が使っている GPU をそのまま使います。YMM4 が内蔵 GPU で動いていると、`nvEncOpenEncodeSessionEx failed (1)` のようなエラーになります。Windows の「グラフィック設定」で `YukkuriMovieMaker.exe` を高パフォーマンス GPU (NVIDIA) に固定してください。手順は [公式 FAQ](https://manjubox.net/ymm4/faq/startup_trouble/select_gpu/) にあります。

## ログと診断

「デバッグログを書き出す」を有効にすると、出力ファイルと同じ場所に `.nvenc_log.txt` を書きます。

設定画面の「プロファイリング結果を書き出す」と「計測専用: 映像・音声を破棄」は、性能調査用です。通常の出力では使いません。

## ライセンス

MIT。利用は自己責任でお願いします。NVIDIA NVENC SDK と YMM4 本体は同梱しません。それぞれの規約に従ってください。

作者 (上流): たるたる [@tarutaru247](https://x.com/tarutaru247)
