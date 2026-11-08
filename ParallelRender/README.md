# ParallelRender

YMM4 の出力プラグイン「ParallelRender (並列描画 + NVENC)」です。映像を複数のスレッドで並列に描画し、GPU のメモリに置いたまま NVENC でエンコードして mp4 にします。仕組みと計測結果は[ルートの README](../README.md) にあります。

## 設定

YMM4 の出力プラグインの設定画面で変えます。並列度、チャンク長、GPU 直渡し、コーデック、品質、レート制御、ビットレートなどを指定できます。

ヘッドレス実行 (`YukkuriMovieMaker.exe --encode`) では設定画面を開けないので、環境変数で指定します。環境変数は設定画面の値より優先されます。

| 環境変数 | 指定できる値 | 意味 |
|---|---|---|
| `PR_DISABLE` | `1` | 並列描画をやめ、1 フレームずつ書き出す |
| `PR_P` | 0〜32 | 並列度。0 は自動 (論理 CPU 数と 4 の小さいほう) |
| `PR_CHUNK` | 1 以上 | チャンク長 (フレーム数)。既定は 250 |
| `PR_ENC_SESSIONS` | 1〜8 | NVENC のセッション数。既定は 2。複数セッションは H.264 と HEVC だけ |
| `PR_GPU_DIRECT` | `1` / `0` | GPU 直渡しを常に使う / 使わない。未指定なら自動 |
| `PR_TEXTCACHE` | `0` | 字幕の描画結果を使い回さない |
| `PR_CODEC` | `h264`、`hevc`、`av1` | コーデック |
| `PR_QUALITY` | `speed`、`balanced`、`quality` | エンコードの品質 |
| `PR_BITRATE` | 100〜200000 (kbps) | ビットレート。指定するとレート制御は「固定」になる |
| `PR_RC` | `fixed`、`variable`、`youtuberecommended` | レート制御 |
| `PR_LOG` | ファイルのパス | ログの出力先。既定は `%TEMP%\pr_log.txt` |

## 制限

- NVIDIA GPU (NVENC) が必要です。出力は mp4 だけで、縦横の解像度は偶数に限ります。
- 並列描画を始めたあとで失敗したときやキャンセルしたときは、出力ファイルを削除します。

## ライセンス

`ParallelRenderPlugin/Encoding/` は NVEnc プラグイン (MIT) のコードを元にしています。[LICENSE](LICENSE) と [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) を参照してください。
