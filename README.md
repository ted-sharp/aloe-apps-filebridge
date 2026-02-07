# Aloe.Apps.FileBridge

- 特定のディレクトリの監視および別 exe の起動
- Blazor で WEB からログを表示
- ネットワークドライブ越しのファイルコピーを安全に検知
- ファイルシステムイベントとポーリングの組み合わせで確実に検知

## 必要な環境

- .NET 10.0

## ソリューション構成

- **Aloe.Apps.FileBridge** … メインアプリ（Blazor + SignalR、監視・exe 起動・WEB ログ表示）
- **Aloe.Apps.FileBridgeLib** … 監視・起動・ログ用のライブラリ
- **Aloe.Apps.FileBridge.Dummy** … テスト用サンプル exe（引数で渡されたファイル/フォルダを同階層の `Processed` に移動）

## ビルド・実行

- **ビルド**: `src` ディレクトリで `dotnet build Aloe.Apps.FileBridge.slnx`
- **実行**: `dotnet run --project Aloe/Apps/FileBridge/Aloe.Apps.FileBridge/Aloe.Apps.FileBridge.csproj`（または Visual Studio 等でメインプロジェクトを起動）
- **開発時の URL**: HTTP `http://localhost:5046` / HTTPS `https://localhost:7060`
- **公開**: メインプロジェクトを `dotnet publish` で公開可能。ルートの `src/_publish.cmd` は別構成（FileBridgeServer/FileBridgeClient 等）を参照しているため、現行ソリューションでは未使用です。

## WEB UI

Blazor で操作ログの表示・設定の確認ができます。起動後、上記の開発時 URL でブラウザからアクセスしてください。

## 設定（FileBridge）

`appsettings.json` の **FileBridge** セクションで監視ディレクトリ・起動 exe・引数などを指定します。  
exe の引数（`Arguments`）では `{FilePath}`（ファイルパス）と `{FolderPath}`（フォルダパス）が利用できます。

### 設定項目一覧

| 項目 | 説明 |
|------|------|
| `WatchDirectory` | 監視対象ディレクトリのパス |
| `PollingIntervalSeconds` | ポーリング間隔（秒） |
| `ExecutablePath` | 起動する exe のパス |
| `Arguments` | exe に渡す引数（`{FilePath}` / `{FolderPath}` が利用可能） |
| `LogDirectory` | 操作ログの保存ディレクトリ |
| `LogRetentionDays` | 操作ログの保持日数 |
| `MaxLogsPerFile` | 1 ファイルあたりの最大ログ数 |
| `IgnoreExtensions` | 無視する拡張子（一時ファイル用。先頭の `.` は省略可） |
| `MarkerFilePatterns` | マーカーファイルパターン（`*.ready` 形式）。指定時はマーカー検知時のみ処理 |
| `SizeCheckIntervalMs` | サイズ安定チェックの間隔（ミリ秒）。0 で無効 |
| `SizeStabilityCheckCount` | 連続で同じサイズが検出された回数で書き込み完了と判断。0 で無効 |
| `MaxConcurrentProcesses` | 同時起動プロセスの最大数。0 で無制限 |

## Dummy プロジェクト

`Aloe.Apps.FileBridge.Dummy` は動作確認用のサンプル exe です。引数にファイルまたはフォルダのパスを渡すと、その親ディレクトリに `Processed` フォルダを作成し、指定されたファイル/フォルダをそこへ移動します。FileBridge の `ExecutablePath` に Dummy を指定してテストできます。
