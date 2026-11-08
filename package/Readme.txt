YMM4 Fast Export
YMM4 の動画書き出しを速くするプラグインです。インストールしただけでは速くなりません。使うプラグインごとに次の設定が必要です。
1. 書き出しの形式に「ParallelRender (並列描画 + NVENC)」を選ぶ。
2. 設定の「プラグイン」の画面から「VideoDecode (非参照フレームのスキップ)」を FFmpeg より上に移動する。
3. 立ち絵の種類を「PSD立ち絵 (高速化版)」にする。既存のプロジェクトは、YMM4 を終了してから ymmp ファイルを FastPsdTachiePlugin\tools\use_fast_tachie.bat にドラッグ＆ドロップすると、まとめて切り替わる。
動作環境: Windows 11、NVIDIA GPU (ドライバー 522.25 以降)
詳しい説明: https://github.com/omi-key/YMM4_FastExport
