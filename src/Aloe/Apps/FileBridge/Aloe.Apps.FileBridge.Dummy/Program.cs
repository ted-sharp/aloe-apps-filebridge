const string ProcessedFolderName = "Processed";

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Dummy: 引数にファイルまたはフォルダのパスを指定してください。");
    return 1;
}

var targetPath = args[0].Trim().Trim('"');

if (!Path.Exists(targetPath))
{
    Console.Error.WriteLine($"Dummy: パスが存在しません: {targetPath}");
    return 1;
}

try
{
    var parentDir = Path.GetDirectoryName(targetPath);
    if (string.IsNullOrEmpty(parentDir))
    {
        Console.Error.WriteLine($"Dummy: 親ディレクトリを取得できません: {targetPath}");
        return 1;
    }

    var processedDir = Path.Combine(parentDir, ProcessedFolderName);
    Directory.CreateDirectory(processedDir);

    string destinationPath;
    if (File.Exists(targetPath))
    {
        var fileName = Path.GetFileName(targetPath);
        destinationPath = GetUniqueDestinationPath(processedDir, fileName, isFile: true);
        File.Move(targetPath, destinationPath);
        Console.WriteLine($"Dummy: ファイルを移動しました: {targetPath} -> {destinationPath}");
    }
    else if (Directory.Exists(targetPath))
    {
        var dirName = Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(dirName))
        {
            Console.Error.WriteLine($"Dummy: フォルダ名を取得できません: {targetPath}");
            return 1;
        }
        destinationPath = GetUniqueDestinationPath(processedDir, dirName, isFile: false);
        Directory.Move(targetPath, destinationPath);
        Console.WriteLine($"Dummy: フォルダを移動しました: {targetPath} -> {destinationPath}");
    }
    else
    {
        Console.Error.WriteLine($"Dummy: ファイルでもフォルダでもありません: {targetPath}");
        return 1;
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Dummy: エラー - {ex.Message}");
    return 1;
}

static string GetUniqueDestinationPath(string baseDir, string name, bool isFile)
{
    var destination = Path.Combine(baseDir, name);
    if (!Path.Exists(destination))
        return destination;

    var stem = isFile ? Path.GetFileNameWithoutExtension(name) : name;
    var extension = isFile ? Path.GetExtension(name) : "";
    var suffix = DateTime.Now.ToString("yyyyMMdd_HHmmss");

    for (var i = 0; i < 1000; i++)
    {
        var candidateName = i == 0
            ? $"{stem}_{suffix}{extension}"
            : $"{stem}_{suffix}_{i}{extension}";
        var candidatePath = isFile
            ? Path.Combine(baseDir, candidateName)
            : Path.Combine(baseDir, candidateName);
        if (!Path.Exists(candidatePath))
            return candidatePath;
    }

    return Path.Combine(baseDir, $"{stem}_{suffix}_{Guid.NewGuid():N}{extension}");
}
