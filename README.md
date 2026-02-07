# Aloe.Apps.FileBridge

- 特定のディレクトリの監視および別 exe の起動
- Blazor で WEB からログを表示
- ネットワークドライブ越しのファイルコピーを安全に検知
- ファイルシステムイベントとポーリングの組み合わせで確実に検知

## 設定

`appsettings.json` の **FileBridge** セクションで監視ディレクトリ・起動 exe・引数などを指定します。  
exe の引数では `{FilePath}`（ファイルパス）と `{FolderPath}`（フォルダパス）が利用できます。



