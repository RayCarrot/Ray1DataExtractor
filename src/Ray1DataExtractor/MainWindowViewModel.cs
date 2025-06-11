using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BinarySerializer;
using BinarySerializer.Audio;
using BinarySerializer.Audio.RIFF;
using BinarySerializer.Ray1;
using BinarySerializer.Ray1.PC;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageMagick;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Ray1DataExtractor;

public partial class MainWindowViewModel : ObservableObject
{
    #region Properties

    [ObservableProperty]
    public partial string? LogText { get; set; }

    #endregion

    #region Helper Methods

    private void Log(string message)
    {
        LogText += message;
        LogText += Environment.NewLine;
    }

    private static string GetExportFilePath(string dir, string fileName)
    {
        string dirPath = Path.Combine("Export", dir);
        Directory.CreateDirectory(dirPath);
        return Path.Combine(dirPath, fileName);
    }

    private async Task ExportFromGamesAsync(Action<Game> exportAction)
    {
        OpenFolderDialog fileDialog = new()
        {
            Multiselect = true,
        };

        if (fileDialog.ShowDialog() != true)
            return;

        Log($"Exporting from {fileDialog.FolderNames.Length} games");

        foreach (string gamePath in fileDialog.FolderNames)
        {
            using Context context = new(String.Empty, new SerializerSettings()
            {
                DefaultStringEncoding = Ray1Settings.DefaultEncoding
            });
            context.AddSettings(new Ray1Settings(Ray1EngineVersion.PC_Edu));

            foreach (string gameVolumeDir in Directory.GetDirectories(Path.Combine(gamePath, "PCMAP")))
            {
                // Get the volume, for example GB1
                string volumeName = Path.GetFileName(gameVolumeDir).ToUpperInvariant();
                context.GetRequiredSettings<Ray1Settings>().Volume = volumeName;
                
                // Get the engine version
                Ray1EngineVersion engineVersion;
                if (File.Exists(Path.Combine(gamePath, "SYSTEM.CNF")))
                    engineVersion = Ray1EngineVersion.PS1_Edu;
                else if (File.Exists(Path.Combine(gamePath, "RAYKIT.EXE")))
                    engineVersion = Ray1EngineVersion.PC_Kit;
                else if (File.Exists(Path.Combine(gamePath, "RAYFAN.EXE")) || File.Exists(Path.Combine(gamePath, "RAYPLUS.EXE")))
                    engineVersion = Ray1EngineVersion.PC_Fan;
                else
                    engineVersion = Ray1EngineVersion.PC_Edu;

                Game game = new(gamePath, volumeName, $"{Path.GetFileName(gamePath)} - {volumeName}", context, engineVersion);

                await Task.Run(() => exportAction(game));

                Log($"Finished exporting {game.ExportName}");
            }
        }

        Log("Finished exporting from all games");
    }

    private static T ReadFile<T>(Context context, string filePath)
        where T : BinarySerializable, new()
    {
        if (!context.FileExists(filePath))
            context.AddFile(new LinearFile(context, filePath));

        return FileFactory.Read<T>(context, filePath);
    }

    private static FileArchive ReadCommonArchive(Game game)
    {
        string filePath = game.EngineVersion == Ray1EngineVersion.PS1_Edu
            ? Path.Combine(game.Path, "PCMAP", game.Volume, "COMMON.DAT")
            : Path.Combine(game.Path, "PCMAP", "COMMON.DAT");

        return ReadFile<FileArchive>(game.Context, filePath);
    }

    private static FileArchive ReadSpecialArchive(Game game)
    {
        string filePath = Path.Combine(game.Path, "PCMAP", game.Volume, "SPECIAL.DAT");

        return ReadFile<FileArchive>(game.Context, filePath);
    }

    #endregion

    #region Commands

    [RelayCommand]
    private async Task ExportSoundSamplesAsync()
    {
        await ExportFromGamesAsync(game =>
        {
            FileArchive sndSmpArchive = ReadFile<FileArchive>(game.Context, Path.Combine(game.Path, "PCMAP", game.Volume, "SNDSMP.DAT"));

            foreach (FileArchiveEntry archiveEntry in sndSmpArchive.Entries)
            {
                byte[] rawData = sndSmpArchive.ReadFileBytes(game.Context, archiveEntry.FileName);

                WAV wav = new();
                RIFF_Chunk_Format fmt = wav.Format;
                fmt.FormatType = 1;
                fmt.ChannelCount = 1;
                fmt.SampleRate = 11025;
                fmt.BitsPerSample = 8;
                wav.Data.Data = rawData;

                fmt.ByteRate = (fmt.SampleRate * fmt.BitsPerSample * fmt.ChannelCount) / 8;
                fmt.BlockAlign = (ushort)((fmt.BitsPerSample * fmt.ChannelCount) / 8);

                // Get the output path
                string outputFilePath = GetExportFilePath(Path.Combine("Sounds", game.ExportName), $"{archiveEntry.FileName}.wav");

                // Create and open the output file
                using FileStream outputStream = File.Create(outputFilePath);
                using Context wavContext = new(String.Empty);

                // Create a key
                const string wavKey = "wav";

                // Add the file to the context
                wavContext.AddFile(new StreamFile(wavContext, wavKey, outputStream));

                // Write the data
                FileFactory.Write<WAV>(wavContext, wavKey, wav);
            }
        });
    }

    [RelayCommand]
    private async Task ExportMapsAsync()
    {
        Dictionary<string, List<string>> levelHashes = new();
        List<string> gameNames = new();
        int currentGameIndex = 0;

        await ExportFromGamesAsync(game =>
        {
            foreach (string lvlFile in Directory.GetFiles(Path.Combine(game.Path, "PCMAP", game.Volume), "*.lev", SearchOption.AllDirectories))
            {
                LevelFile level = ReadFile<LevelFile>(game.Context, lvlFile);

                // Get all tile textures
                List<BlockTexture> blockTextures = level.NormalBlockTextures.OpaqueTextures.
                    Concat(level.NormalBlockTextures.TransparentTextures).
                    ToList();

                const int tileSize = 16;

                int width = level.MapInfo.Width * tileSize;
                int height = level.MapInfo.Height * tileSize;

                byte[] rawImgData = new byte[width * height * 4];

                for (int tileY = 0; tileY < level.MapInfo.Height; tileY++)
                {
                    for (int tileX = 0; tileX < level.MapInfo.Width; tileX++)
                    {
                        Block block = level.MapInfo.Blocks[tileY * level.MapInfo.Width + tileX];
                        BlockTexture? blockTexture = blockTextures.FirstOrDefault(x =>
                            x.Offset == level.NormalBlockTextures.NormalBlockTexturesOffsetTable[block.TileIndex]);

                        for (int y = 0; y < tileSize; y++)
                        {
                            for (int x = 0; x < tileSize; x++)
                            {
                                BaseColor color = blockTexture == null || block.TileIndex == 0 || blockTexture.TransparencyMode == 0xAAAAAAAA
                                    ? new CustomColor(0, 0, 0, 0)
                                    : level.MapInfo.Palettes[0][255 - blockTexture.ImgData[y * tileSize + x]];

                                int rawImgDataIndex = (tileY * tileSize + y) * width + (tileX * tileSize + x);

                                rawImgData[rawImgDataIndex * 4 + 0] = (byte)(color.Red * Byte.MaxValue);
                                rawImgData[rawImgDataIndex * 4 + 1] = (byte)(color.Green * Byte.MaxValue);
                                rawImgData[rawImgDataIndex * 4 + 2] = (byte)(color.Blue * Byte.MaxValue);
                                if (blockTexture is TransparentBlockTexture transparent)
                                    rawImgData[rawImgDataIndex * 4 + 3] = transparent.Alpha[y * tileSize + x];
                                else
                                    rawImgData[rawImgDataIndex * 4 + 3] = (byte)(color.Alpha * Byte.MaxValue);
                            }
                        }
                    }
                }

                using MagickImage img = new(rawImgData, new MagickReadSettings()
                {
                    Format = MagickFormat.Rgba,
                    Width = (uint?)width,
                    Height = (uint?)height
                });

                // Get the output path
                string levelName = Path.GetFileNameWithoutExtension(lvlFile).ToUpperInvariant();
                string outputFilePath = GetExportFilePath(Path.Combine("Maps", game.ExportName), $"{levelName}.png");

                if (!levelHashes.ContainsKey(levelName))
                    levelHashes[levelName] = new List<string>();

                using SHA512 sha512 = SHA512.Create();
                string hash = Convert.ToBase64String(sha512.ComputeHash(rawImgData));

                while (levelHashes[levelName].Count < currentGameIndex)
                    levelHashes[levelName].Add(String.Empty);

                levelHashes[levelName].Insert(currentGameIndex, hash);

                img.Write(outputFilePath);
            }

            gameNames.Add(game.ExportName);
            currentGameIndex++;
        });

        StringBuilder sb = new();
        void addValue(string value) => sb.Append($"{value},");

        addValue("Level");

        foreach (string gameName in gameNames)
            addValue(gameName);

        sb.AppendLine();

        foreach (KeyValuePair<string, List<string>> keyValuePair in levelHashes.OrderBy(x => x.Key))
        {
            addValue(keyValuePair.Key);
            foreach (string hash in keyValuePair.Value)
                addValue(hash);

            sb.AppendLine();
        }

        await File.WriteAllTextAsync(GetExportFilePath("Maps", "Hashes.csv"), sb.ToString());
    }

    [RelayCommand]
    private async Task ExportLevelSheetAsync()
    {
        StringBuilder sb = new();

        void writeValue(string str)
        {
            str = str.Trim('/');
            sb.Append($"\"{str}\",");
        }

        writeValue("Game");
        writeValue("Level Name 1");
        writeValue("Level Name 2");
        writeValue("Part 1");
        writeValue("Part 2");
        writeValue("Part 3");
        writeValue("Part 4");
        writeValue("Part 5");
        writeValue("Part 6");
        sb.AppendLine();

        await ExportFromGamesAsync(game =>
        {
            FileArchive specialArchive = ReadSpecialArchive(game);

            WorldMapScript worldMap = specialArchive.ReadFile<WorldMapScript>(game.Context, "WLDMAP01");
            TextScript text = specialArchive.ReadFile<TextScript>(game.Context, "TEXT");

            foreach (WorldInfo worldInfo in worldMap.WorldMap.MapDefine)
            {
                writeValue(game.ExportName);
                writeValue(text.PCPacked_TextDefine[worldInfo.WorldName]);
                writeValue(text.PCPacked_TextDefine[worldInfo.LevelName]);

                foreach (LevelLinkEntry levelLink in worldInfo.LevelLinks[0])
                {
                    if (levelLink.LevelVariants[0] == null)
                        writeValue("");
                    else
                        writeValue($"{worldInfo.World} {levelLink.LevelVariants[0]}");
                }

                sb.AppendLine();
            }
        });

        await File.WriteAllTextAsync(GetExportFilePath("LevelSheet", "Levels.csv"), sb.ToString());
    }

    [RelayCommand]
    private async Task ExportVersionsSheetAsync()
    {
        StringBuilder sb = new();

        void writeValue(string str)
        {
            str = str.Trim('/');
            sb.Append($"\"{str}\",");
        }

        writeValue("Game");
        writeValue("Version Codes");
        writeValue("Version Modes");
        writeValue("Languages");
        sb.AppendLine();

        await ExportFromGamesAsync(game =>
        {
            FileArchive commonArchive = ReadCommonArchive(game);
            FileArchive specialArchive = ReadSpecialArchive(game);

            VersionScript version = commonArchive.ReadFile<VersionScript>(game.Context, "VERSION");
            TextScript text = specialArchive.ReadFile<TextScript>(game.Context, "TEXT");

            writeValue(game.ExportName);
            writeValue(String.Join(Environment.NewLine, version.VersionCodes));
            writeValue(String.Join(Environment.NewLine, version.VersionModes));
            writeValue(String.Join(Environment.NewLine, text.LanguageNames));

            sb.AppendLine();
        });

        await File.WriteAllTextAsync(GetExportFilePath("Versions", "Versions.csv"), sb.ToString());
    }

    [RelayCommand]
    private async Task ExportScriptsAsync()
    {
        await ExportFromGamesAsync(game =>
        {
            FileArchive commonArchive = ReadCommonArchive(game);
            FileArchive specialArchive = ReadSpecialArchive(game);

            SerializeScript<VersionScript>(game, commonArchive, "VERSION");
            
            GeneralScript generalScript = SerializeScript<GeneralScript>(game, specialArchive, "GENERAL");
            SerializeScript<WordsScript>(game, specialArchive, "MOT");
            SerializeScript<TextScript>(game, specialArchive, "TEXT");
            SerializeScript<WorldMapScript>(game, specialArchive, $"WLDMAP{generalScript.WorldMapNumber:00}");
            SerializeScript<SampleNamesScript>(game, specialArchive, "SMPNAMES");
        });

        static T SerializeScript<T>(Game game, FileArchive archive, string name)
            where T : BinarySerializable, new()
        {
            T script = archive.ReadFile<T>(game.Context, name);

            string json = JsonConvert.SerializeObject(script, Formatting.Indented, new StringEnumConverter(), new ByteArrayHexConverter());

            File.WriteAllText(GetExportFilePath(Path.Combine("Scripts", game.ExportName), $"{name}.json"), json);

            return script;
        }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        Process.Start(new ProcessStartInfo()
        {
            FileName = GetExportFilePath(String.Empty, String.Empty),
            UseShellExecute = true,
            Verb = "open"
        });
    }

    #endregion

    #region Data Types

    private record Game(string Path, string Volume, string ExportName, Context Context, Ray1EngineVersion EngineVersion);

    #endregion
}