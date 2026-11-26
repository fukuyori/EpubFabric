# NDLOCR-Lite のライセンス文書

EpubFabric は、国立国会図書館の NDLOCR-Lite の学習済みモデル（レイアウト認識の DEIM と文字認識の PARSeq 3種）と文字集合（`NDLmoji.yaml`）を同梱する。このフォルダーには、取得元のリポジトリにあるライセンス文書を変更せずに収めている。

- 取得元: https://github.com/ndl-lab/ndlocr-lite （v2.0.0、コミット `ba8bb85`）
- `LICENCE`: NDLOCR-Lite 本体のライセンス（CC BY 4.0）
- `LICENCE_DEPENDENCEIES`: 依存物のライセンス。末尾の「Weights of DEIMv2 / DINOv3 License」は、同梱するレイアウト認識モデル `deim-s-1024x1024-261124.onnx` に適用される

NDLOCR-Lite の README は、同梱の学習済みモデルを使う場合に DINOv3 License も守るよう求めている。DINOv3 License 1.b.i は、モデルを配布する際にライセンスの写しを添えることを求めている。そのため、配布物の `models\ndlocr` フォルダーにもこのフォルダーの内容を同梱する。
