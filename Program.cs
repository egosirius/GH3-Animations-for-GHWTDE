using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GH3AnimationsForGHWTDE;

internal static class Program
{
    private const string Magic = "GH3ANM1\0";
    private const int MinMatch = 16;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private sealed class Manifest
    {
        public int FormatVersion { get; set; } = 1;
        public string Name { get; set; } = "GH3 Animations for GHWTDE";
        public string PackFolder { get; set; } = "Guitar Hero 3 Legends Of Rock";
        public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("O");
        public List<ManifestSong> Songs { get; set; } = [];
    }

    private sealed class ManifestSong
    {
        public int Index { get; set; }
        public string Song { get; set; } = "";
        public string Folder { get; set; } = "";
        public string PakFile { get; set; } = "";
        public string OriginalSha256 { get; set; } = "";
        public string PatchedSha256 { get; set; } = "";
        public string PatchFile { get; set; } = "";
    }

    private sealed class ExistingReport
    {
        public List<ExistingSong> Songs { get; set; } = [];
    }

    private sealed class ExistingSong
    {
        public int Index { get; set; }
        public string Song { get; set; } = "";
        public string TargetPak { get; set; } = "";
        public string BackupPak { get; set; } = "";
    }

    private abstract record DeltaCommand;
    private sealed record CopyCommand(long Offset, int Length) : DeltaCommand;
    private sealed record AddCommand(byte[] Data) : DeltaCommand;

    private sealed record PlannedChange(ManifestSong Song, string Target, bool AlreadyDesired);

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "GH3 Animations for GHWTDE";
        try
        {
            if (args.Length == 0)
                return Help();

            return args[0].ToLowerInvariant() switch
            {
                "install" => ChangeInstallation(args, install: true),
                "uninstall" => ChangeInstallation(args, install: false),
                "make-patches" => MakePatches(args),
                "verify" => VerifyPackage(args),
                "test-patches" => TestPatches(args),
                _ => Help()
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine();
            Console.Error.WriteLine("ERRO: " + ex.Message);
            Console.ResetColor();
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("GH3 Animations for GHWTDE");
        Console.WriteLine("  install <pasta-do-pacote> [pasta-DATA\\MODS]");
        Console.WriteLine("  uninstall <pasta-do-pacote> [pasta-DATA\\MODS]");
        Console.WriteLine("  verify <pasta-do-pacote>");
        Console.WriteLine("  make-patches <install-report.json> <pasta-do-pacote>");
        Console.WriteLine("  test-patches <install-report.json> <pasta-do-pacote>");
        return 2;
    }

    private static int ChangeInstallation(string[] args, bool install)
    {
        if (args.Length is < 2 or > 3)
            throw new ArgumentException($"Uso: {(install ? "install" : "uninstall")} <pasta-do-pacote> [pasta-DATA\\MODS]");
        if (Process.GetProcessesByName("GHWT_Definitive").Length != 0)
            throw new InvalidOperationException("O Guitar Hero World Tour Definitive Edition está aberto. Feche o jogo e tente novamente.");

        var packageRoot = Path.GetFullPath(args[1]);
        var manifest = ReadManifest(packageRoot);
        var modsRoot = args.Length == 3 ? Path.GetFullPath(args[2]) : Directory.GetParent(packageRoot)?.FullName
            ?? throw new DirectoryNotFoundException("Não foi possível localizar DATA\\MODS.");
        var packRoot = Path.GetFullPath(Path.Combine(modsRoot, manifest.PackFolder));
        if (!Directory.Exists(packRoot))
            throw new DirectoryNotFoundException($"Pack não encontrado: {packRoot}");

        Console.WriteLine(install ? "INSTALADOR — GH3 Animations for GHWTDE" : "DESINSTALADOR — GH3 Animations for GHWTDE");
        Console.WriteLine($"Pack: {packRoot}");
        Console.WriteLine("Verificando 75 músicas antes de alterar qualquer arquivo...");

        var plan = new List<PlannedChange>();
        foreach (var song in manifest.Songs.OrderBy(x => x.Index))
        {
            var target = SafeSongPath(packRoot, song);
            if (!File.Exists(target))
                throw new FileNotFoundException($"Arquivo da música não encontrado: {song.Song}", target);
            var currentHash = Sha256(target);
            var baseHash = install ? song.OriginalSha256 : song.PatchedSha256;
            var desiredHash = install ? song.PatchedSha256 : song.OriginalSha256;
            if (!currentHash.Equals(baseHash, StringComparison.OrdinalIgnoreCase) &&
                !currentHash.Equals(desiredHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Versão desconhecida de '{song.Song}'. Nenhum arquivo foi alterado. Hash: {currentHash}");
            plan.Add(new PlannedChange(song, target, currentHash.Equals(desiredHash, StringComparison.OrdinalIgnoreCase)));
        }

        var pending = plan.Where(x => !x.AlreadyDesired).ToList();
        if (pending.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(install ? "As 75 músicas já estão instaladas." : "As 75 músicas já estão no estado original.");
            Console.ResetColor();
            return 0;
        }

        var backupRoot = Path.Combine(packageRoot, "Backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(backupRoot);
        var completed = new List<PlannedChange>();
        try
        {
            foreach (var item in pending)
            {
                var relative = Path.GetRelativePath(packRoot, item.Target);
                var backup = Path.Combine(backupRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(item.Target, backup, overwrite: false);

                var patchPath = SafePackagePath(packageRoot, item.Song.PatchFile);
                var patch = ReadPatch(patchPath);
                var input = File.ReadAllBytes(item.Target);
                var output = ApplyDelta(input, install ? patch.Forward : patch.Reverse);
                var expected = install ? item.Song.PatchedSha256 : item.Song.OriginalSha256;
                if (!Hash(output).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Validação falhou em '{item.Song.Song}'.");

                var temporary = item.Target + ".drumkitgh3.tmp";
                File.WriteAllBytes(temporary, output);
                File.Move(temporary, item.Target, overwrite: true);
                completed.Add(item);
                Console.WriteLine($"[{completed.Count}/{pending.Count}] {item.Song.Song}");
            }
        }
        catch
        {
            Console.WriteLine("Falha detectada. Restaurando os arquivos já processados...");
            foreach (var item in completed.AsEnumerable().Reverse())
            {
                var backup = Path.Combine(backupRoot, Path.GetRelativePath(packRoot, item.Target));
                if (File.Exists(backup)) File.Copy(backup, item.Target, overwrite: true);
            }
            throw;
        }

        var state = new
        {
            Action = install ? "install" : "uninstall",
            CompletedAt = DateTimeOffset.Now.ToString("O"),
            Changed = completed.Count,
            AlreadyDesired = plan.Count - completed.Count,
            Backup = backupRoot
        };
        File.WriteAllText(Path.Combine(packageRoot, "last-operation.json"), JsonSerializer.Serialize(state, JsonOptions), new UTF8Encoding(false));
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine(install
            ? $"INSTALAÇÃO CONCLUÍDA: {completed.Count} alteradas, {plan.Count - completed.Count} já instaladas."
            : $"DESINSTALAÇÃO CONCLUÍDA: {completed.Count} restauradas, {plan.Count - completed.Count} já originais.");
        Console.WriteLine($"Backup de segurança: {backupRoot}");
        Console.ResetColor();
        return 0;
    }

    private static int MakePatches(string[] args)
    {
        if (args.Length != 3)
            throw new ArgumentException("Uso: make-patches <install-report.json> <pasta-do-pacote>");
        var reportPath = Path.GetFullPath(args[1]);
        var packageRoot = Path.GetFullPath(args[2]);
        var report = JsonSerializer.Deserialize<ExistingReport>(File.ReadAllText(reportPath), JsonOptions)
            ?? throw new InvalidDataException("Relatório inválido.");
        if (report.Songs.Count != 75)
            throw new InvalidDataException($"O relatório deveria conter 75 músicas; contém {report.Songs.Count}.");

        var movements = Path.Combine(packageRoot, "Movements");
        Directory.CreateDirectory(movements);
        var manifest = new Manifest();
        foreach (var item in report.Songs.OrderBy(x => x.Index))
        {
            var original = File.ReadAllBytes(item.BackupPak);
            var patched = File.ReadAllBytes(item.TargetPak);
            var folder = Directory.GetParent(Path.GetDirectoryName(item.TargetPak)!)?.Name
                ?? throw new InvalidDataException("Estrutura inesperada: " + item.TargetPak);
            var pakFile = Path.GetFileName(item.TargetPak);
            var patchName = $"{item.Index:000}_{Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(pakFile))}.gh3anim";
            var patchRelative = Path.Combine("Movements", patchName).Replace('\\', '/');
            var patchPath = Path.Combine(packageRoot, patchRelative.Replace('/', Path.DirectorySeparatorChar));
            var forward = BuildDelta(original, patched);
            var reverse = BuildDelta(patched, original);
            WritePatch(patchPath, original, patched, forward, reverse);
            manifest.Songs.Add(new ManifestSong
            {
                Index = item.Index,
                Song = item.Song,
                Folder = folder,
                PakFile = pakFile,
                OriginalSha256 = Hash(original),
                PatchedSha256 = Hash(patched),
                PatchFile = patchRelative
            });
            Console.WriteLine($"[{item.Index:00}/75] {item.Song} — {new FileInfo(patchPath).Length:N0} bytes");
        }
        File.WriteAllText(Path.Combine(packageRoot, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
        return VerifyPackage(["verify", packageRoot]);
    }

    private static int VerifyPackage(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Uso: verify <pasta-do-pacote>");
        var root = Path.GetFullPath(args[1]);
        var manifest = ReadManifest(root);
        if (manifest.FormatVersion != 1 || manifest.Songs.Count != 75)
            throw new InvalidDataException("Manifesto incompleto ou incompatível.");
        long bytes = 0;
        foreach (var song in manifest.Songs)
        {
            var path = SafePackagePath(root, song.PatchFile);
            var patch = ReadPatch(path);
            if (!Convert.ToHexString(patch.OriginalHash).Equals(song.OriginalSha256, StringComparison.OrdinalIgnoreCase) ||
                !Convert.ToHexString(patch.PatchedHash).Equals(song.PatchedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Hashes divergentes no movimento de " + song.Song);
            bytes += new FileInfo(path).Length;
        }
        Console.WriteLine($"PACOTE VÁLIDO: 75/75 movimentos, {bytes:N0} bytes.");
        return 0;
    }

    private static int TestPatches(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Uso: test-patches <install-report.json> <pasta-do-pacote>");
        var report = JsonSerializer.Deserialize<ExistingReport>(File.ReadAllText(Path.GetFullPath(args[1])), JsonOptions)
            ?? throw new InvalidDataException("Relatório inválido.");
        var packageRoot = Path.GetFullPath(args[2]);
        var manifest = ReadManifest(packageRoot);
        foreach (var sourceSong in report.Songs.OrderBy(x => x.Index))
        {
            var song = manifest.Songs.Single(x => x.Index == sourceSong.Index);
            var original = File.ReadAllBytes(sourceSong.BackupPak);
            var patched = File.ReadAllBytes(sourceSong.TargetPak);
            var patch = ReadPatch(SafePackagePath(packageRoot, song.PatchFile));
            var generatedPatched = ApplyDelta(original, patch.Forward);
            var generatedOriginal = ApplyDelta(patched, patch.Reverse);
            if (!generatedPatched.AsSpan().SequenceEqual(patched))
                throw new InvalidDataException("Patch de instalação divergiu em " + song.Song);
            if (!generatedOriginal.AsSpan().SequenceEqual(original))
                throw new InvalidDataException("Patch de desinstalação divergiu em " + song.Song);
            Console.WriteLine($"[{song.Index:00}/75] {song.Song}");
        }
        Console.WriteLine("TESTE COMPLETO: 75 instalações e 75 desinstalações reconstruíram os arquivos byte por byte.");
        return 0;
    }

    private sealed record PatchData(byte[] OriginalHash, byte[] PatchedHash, long OriginalLength, long PatchedLength,
        List<DeltaCommand> Forward, List<DeltaCommand> Reverse);

    private static List<DeltaCommand> BuildDelta(byte[] source, byte[] target)
    {
        var index = new Dictionary<ulong, List<int>>();
        for (var offset = 0; offset + MinMatch <= source.Length; offset += MinMatch)
        {
            var key = BitConverter.ToUInt64(source, offset);
            if (!index.TryGetValue(key, out var positions)) index[key] = positions = [];
            if (positions.Count < 12) positions.Add(offset);
            else positions[^1] = offset;
        }

        var commands = new List<DeltaCommand>();
        var additions = new List<byte>();
        var position = 0;
        while (position < target.Length)
        {
            var bestOffset = -1;
            var bestLength = 0;
            if (position + MinMatch <= target.Length)
            {
                var key = BitConverter.ToUInt64(target, position);
                if (index.TryGetValue(key, out var candidates))
                {
                    foreach (var candidate in candidates)
                    {
                        var max = Math.Min(source.Length - candidate, target.Length - position);
                        var length = 0;
                        while (length < max && source[candidate + length] == target[position + length]) length++;
                        if (length > bestLength) { bestLength = length; bestOffset = candidate; }
                    }
                }
            }

            if (bestLength >= MinMatch)
            {
                FlushAdd(commands, additions);
                commands.Add(new CopyCommand(bestOffset, bestLength));
                position += bestLength;
            }
            else
            {
                additions.Add(target[position++]);
            }
        }
        FlushAdd(commands, additions);
        return commands;
    }

    private static void FlushAdd(List<DeltaCommand> commands, List<byte> additions)
    {
        if (additions.Count == 0) return;
        commands.Add(new AddCommand(additions.ToArray()));
        additions.Clear();
    }

    private static byte[] ApplyDelta(byte[] source, List<DeltaCommand> commands)
    {
        using var output = new MemoryStream();
        foreach (var command in commands)
        {
            switch (command)
            {
                case CopyCommand copy:
                    if (copy.Offset < 0 || copy.Length < 0 || copy.Offset + copy.Length > source.LongLength)
                        throw new InvalidDataException("Comando COPY inválido.");
                    output.Write(source, checked((int)copy.Offset), copy.Length);
                    break;
                case AddCommand add:
                    output.Write(add.Data);
                    break;
            }
        }
        return output.ToArray();
    }

    private static void WritePatch(string path, byte[] original, byte[] patched, List<DeltaCommand> forward, List<DeltaCommand> reverse)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(SHA256.HashData(original));
        writer.Write(SHA256.HashData(patched));
        writer.Write((long)original.Length);
        writer.Write((long)patched.Length);
        WriteCompressedCommands(writer, forward);
        WriteCompressedCommands(writer, reverse);
    }

    private static PatchData ReadPatch(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic) throw new InvalidDataException("Arquivo de movimento inválido: " + path);
        var originalHash = reader.ReadBytes(32);
        var patchedHash = reader.ReadBytes(32);
        var originalLength = reader.ReadInt64();
        var patchedLength = reader.ReadInt64();
        return new PatchData(originalHash, patchedHash, originalLength, patchedLength,
            ReadCompressedCommands(reader), ReadCompressedCommands(reader));
    }

    private static void WriteCompressedCommands(BinaryWriter writer, List<DeltaCommand> commands)
    {
        using var raw = new MemoryStream();
        using (var commandWriter = new BinaryWriter(raw, Encoding.UTF8, leaveOpen: true))
        {
            commandWriter.Write(commands.Count);
            foreach (var command in commands)
            {
                switch (command)
                {
                    case CopyCommand copy:
                        commandWriter.Write((byte)0);
                        commandWriter.Write(copy.Offset);
                        commandWriter.Write(copy.Length);
                        break;
                    case AddCommand add:
                        commandWriter.Write((byte)1);
                        commandWriter.Write(add.Data.Length);
                        commandWriter.Write(add.Data);
                        break;
                }
            }
        }
        raw.Position = 0;
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) raw.CopyTo(brotli);
        var data = compressed.ToArray();
        writer.Write(data.Length);
        writer.Write(data);
    }

    private static List<DeltaCommand> ReadCompressedCommands(BinaryReader reader)
    {
        var compressedLength = reader.ReadInt32();
        if (compressedLength <= 0 || compressedLength > 128 * 1024 * 1024) throw new InvalidDataException("Bloco comprimido inválido.");
        using var compressed = new MemoryStream(reader.ReadBytes(compressedLength));
        using var brotli = new BrotliStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        brotli.CopyTo(raw);
        raw.Position = 0;
        using var commandReader = new BinaryReader(raw);
        var count = commandReader.ReadInt32();
        if (count < 0 || count > 10_000_000) throw new InvalidDataException("Quantidade de comandos inválida.");
        var commands = new List<DeltaCommand>(count);
        for (var i = 0; i < count; i++)
        {
            switch (commandReader.ReadByte())
            {
                case 0: commands.Add(new CopyCommand(commandReader.ReadInt64(), commandReader.ReadInt32())); break;
                case 1:
                    var length = commandReader.ReadInt32();
                    if (length < 0 || length > 128 * 1024 * 1024) throw new InvalidDataException("Comando ADD inválido.");
                    commands.Add(new AddCommand(commandReader.ReadBytes(length)));
                    break;
                default: throw new InvalidDataException("Tipo de comando desconhecido.");
            }
        }
        return commands;
    }

    private static Manifest ReadManifest(string root)
    {
        var path = SafePackagePath(root, "manifest.json");
        return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("manifest.json inválido.");
    }

    private static string SafeSongPath(string packRoot, ManifestSong song)
    {
        var path = Path.GetFullPath(Path.Combine(packRoot, song.Folder, "Content", song.PakFile));
        EnsureInside(packRoot, path);
        return path;
    }

    private static string SafePackagePath(string packageRoot, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(packageRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureInside(packageRoot, path);
        return path;
    }

    private static void EnsureInside(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Caminho fora da pasta permitida: " + path);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
