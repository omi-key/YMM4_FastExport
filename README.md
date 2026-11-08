# YMM4 Fast Export

ゆっくりムービーメーカー4 (YMM4) の動画書き出しを速くする、3 つのプラグインです。非公式・無保証です。

| プラグイン | 役割 |
|---|---|
| [ParallelRender](ParallelRender/README.md) | 映像を並列に描画し、NVENC でエンコードする出力プラグイン |
| [VideoDecode](VideoDecode/README.md) | 倍速再生で表示されないフレームのデコードを省く |
| [FastPsdTachie](FastPsdTachie/README.md) | PSD 立ち絵の合成結果を使い回す |

動作環境は Windows 11 と、NVIDIA GPU (ドライバー 522.25 以降) です。

## 導入

[Releases](../../releases) から `YMM4_FastExport.ymme` をダウンロードして開くと、YMM4 のインストーラーが起動します。使わないプラグインは、`<YMM4>\user\plugin\YMM4_FastExport\` の下のフォルダごと削除してかまいません。

インストールしただけでは速くなりません。使うプラグインごとに、次の設定をしてください。

### 1. 出力に ParallelRender を選ぶ

動画の書き出しの画面で、出力プラグインに「ParallelRender (並列描画 + NVENC)」を選びます。

### 2. VideoDecode を FFmpeg より上に置く

YMM4 の設定の「プラグイン」の画面で、「VideoDecode (非参照フレームのスキップ)」を FFmpeg より上に移動します。YMM4 は動画読み込みプラグインを上から順に試すので、下にあると使われません。

### 3. 立ち絵を高速化版に切り替える

キャラクターの立ち絵の種類を「PSD立ち絵 (高速化版)」にします。切り替えるとパラメーターが初期化されることがあるので、PSD とレイヤーの選択を確認してください。

既存のプロジェクトは、YMM4 を終了してから ymmp ファイルを `FastPsdTachiePlugin\tools\use_fast_tachie.bat` にドラッグ＆ドロップすると、全キャラクターがまとめて切り替わります。

- 元のファイルは `<名前>.before_fast.ymmp` として残ります。
- 標準に戻すときは `use_standard_tachie.bat` を使います。`FastPsdTachiePlugin` を削除する前に、高速化版を使っている ymmp をすべて標準に戻してください。

## うまく動かないとき

- **NVENC を開けないというエラーで止まる**: GPU が 2 つあるノート PC では、YMM4 が内蔵 GPU で動いている可能性があります。Windows の「設定」→「システム」→「ディスプレイ」→「グラフィック」で、`YukkuriMovieMaker.exe` を「高パフォーマンス」にしてください ([YMM4 の FAQ](https://manjubox.net/ymm4/faq/startup_trouble/select_gpu/))。
- **書き出しが失敗する、不安定になる**: VRAM が足りない可能性があります。ParallelRender の設定画面で、「並列度」を 2 に下げる、「チャンク長」を 100 程度にする、「GPU 直渡し」を OFF にする、の順に試してください。
- **速くならない**: ParallelRender のログ (`%TEMP%\pr_log.txt`) に `No Scene available` の行があれば、並列にできず 1 フレームずつ書き出しています。
- **YMM4 を更新したら動かなくなった**: プラグインは YMM4 の更新で動かなくなったり、標準の処理に戻ったりする可能性があります。

## 計測結果

作者の環境 (RTX 4080) で、1080p60・25 分のプロジェクトを H.264 で書き出した時間です。

| 構成 | 書き出し時間 |
|---|---|
| 標準の NVEnc 出力 | 約 80 分 |
| + FastPsdTachie | 635 秒 |
| + ParallelRender + VideoDecode | 約 130 秒 |

立ち絵・字幕・画像が多い区間ほど大きく縮み、動画ばかりの区間はあまり縮みません。動画のデコードは GPU の NVDEC で頭打ちになるためです。

## 標準との違い

- 出力の画素は標準と一致しません。字幕の描画結果の使い回し (`PR_TEXTCACHE`) と、2 つの NVENC セッションでのエンコード (`PR_ENC_SESSIONS`) によるもので、どちらも目では見分けられない程度の差です。
- まばたきのタイミングが違います。標準は YMM4 を起動するたびに変わりますが、FastPsdTachie はいつも同じタイミングにします。

## 仕組み

標準の書き出しは、1 つのスレッドでフレームを 1 枚ずつ処理し、時間の大半を CPU の処理に使います。3 つのプラグインは、それぞれ別の処理を減らします。

- **ParallelRender**: 出力範囲をチャンクに分けて複数のスレッドで描画し、GPU のメモリに置いたまま NVENC に渡します。
- **VideoDecode**: 倍速再生で飛ばすフレームのうち、他のフレームから参照されないものをデコードしません。ParallelRender と併用すると、キーフレームの近くにチャンクの境界を置かせて、余分なデコードを減らします。
- **FastPsdTachie**: 有効なレイヤーの組み合わせが前のフレームと同じなら、合成済みの画像をそのまま使います。標準は毎フレームすべてのレイヤーを調べ直します。

ParallelRender が使う `NvencNative.dll` のソースは [`NVEnc/`](NVEnc/README.md) にあります。[tarutaru247/YMM4_NVEncPlugin](https://github.com/tarutaru247/YMM4_NVEncPlugin) を基にした fork です。

## ライセンス

MIT ([LICENSE](LICENSE))。`NVEnc/` には、上流 (tarutaru247) と AMF 版 (tp-li-dev / Disnana) の著作権表示を残した [LICENSE](NVEnc/LICENSE) と [THIRD_PARTY_NOTICES.txt](NVEnc/THIRD_PARTY_NOTICES.txt) があります。ParallelRender の `Encoding/` は NVEnc のコードを元にしています ([ParallelRender/THIRD_PARTY_NOTICES.txt](ParallelRender/THIRD_PARTY_NOTICES.txt))。
