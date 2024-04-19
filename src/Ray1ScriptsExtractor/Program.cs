using System.Reflection;
using System.Text;
using BinarySerializer;
using BinarySerializer.Ray1;
using BinarySerializer.Ray1.PC;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Ray1ScriptsExtractor;

internal class Program
{
    private static void Main(string[] args)
    {
        string src = args[0];
        string dst = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "SCRIPTS");
        Directory.CreateDirectory(dst);

        using Context context = new(src, settings: new SerializerSettings()
        {
            DefaultStringEncoding = Encoding.GetEncoding(437)
        });

        bool isPs1 = File.Exists(Path.Combine(src, "SYSTEM.CNF"));

        context.AddSettings(new Ray1Settings(isPs1 ? Ray1EngineVersion.PS1_Edu : Ray1EngineVersion.PC_Edu));

        string[] versionCodes;
        if (isPs1)
        {
            versionCodes = Directory.GetDirectories(Path.Combine(src, "PCMAP")).Select(Path.GetFileName).ToArray();
        }
        else
        {
            LinearFile commonFile = context.AddFile(new LinearFile(context, "PCMAP/COMMON.DAT"));
            FileArchive commonArchive = FileFactory.Read<FileArchive>(context, commonFile.FilePath);
            VersionScript versionScript = SerializeScript<VersionScript>(context, commonArchive, "VERSION", dst, null);
            versionCodes = versionScript.VersionCodes;
        }

        foreach (string version in versionCodes)
        {
            Ray1Settings settings = context.GetRequiredSettings<Ray1Settings>();
            settings.Volume = version;

            if (isPs1)
            {
                LinearFile commonFile = context.AddFile(new LinearFile(context, $"PCMAP/{version}/COMMON.DAT"));
                FileArchive commonArchive = FileFactory.Read<FileArchive>(context, commonFile.FilePath);
                SerializeScript<VersionScript>(context, commonArchive, "VERSION", dst, version);
            }

            LinearFile specialFile = context.AddFile(new LinearFile(context, $"PCMAP/{version}/SPECIAL.DAT"));
            
            if (!specialFile.SourceFileExists)
                continue;

            FileArchive specialArchive = FileFactory.Read<FileArchive>(context, specialFile.FilePath);

            GeneralScript generalScript = SerializeScript<GeneralScript>(context, specialArchive, "GENERAL", dst, version);
            SerializeScript<WordsScript>(context, specialArchive, "MOT", dst, version);
            TextScript text = SerializeScript<TextScript>(context, specialArchive, "TEXT", dst, version);
            WorldMapScript wld = SerializeScript<WorldMapScript>(context, specialArchive, $"WLDMAP{generalScript.WorldMapNumber:00}", dst, version);
            SerializeScript<SampleNamesScript>(context, specialArchive, "SMPNAMES", dst, version);

            string wldSheet = CreateWorldMapSheet(wld.WorldMap, text);
            string wldSheetDst = Path.Combine(dst, $"{version} - WLDMAP{generalScript.WorldMapNumber:00}.csv");
            File.WriteAllText(wldSheetDst, wldSheet);
        }
    }

    private static T SerializeScript<T>(Context context, FileArchive archive, string name, string dst, string? version)
        where T : BinarySerializable, new()
    {
        T script = archive.ReadFile<T>(context, name);

        string json = JsonConvert.SerializeObject(script, Formatting.Indented, new StringEnumConverter(), new ByteArrayHexConverter());

        dst = Path.Combine(dst, version == null 
            ? $"{name}.json" 
            : $"{version} - {name}.json");

        File.WriteAllText(dst, json);

        return script;
    }

    private static string CreateWorldMapSheet(WorldMap wld, TextScript text)
    {
        StringBuilder sb = new();

        void writeValue(string value) => sb.Append($"{value}\t");

        // Header
        writeValue("Name");
        writeValue("Description");
        writeValue("Type");
        writeValue("World");
        writeValue("Lives");
        writeValue("Level 1");
        writeValue("Level 2");
        writeValue("Level 3");
        writeValue("Level 4");
        writeValue("Level 5");
        writeValue("Demo level");
        sb.AppendLine();

        foreach (WorldInfo info in wld.MapDefine)
        {
            if (info.XPosition == 0)
                continue;

            writeValue(text.PCPacked_TextDefine[info.WorldName].Replace("/", String.Empty));
            writeValue(text.PCPacked_TextDefine[info.LevelName].Replace("/", String.Empty));
            writeValue($"{info.Type}");
            writeValue($"{info.World}");
            writeValue($"{info.LivesCount}");

            for (int i = 0; i < 5; i++)
            {
                string lvls = String.Join(" ", info.LevelLinks[i].Select(x => x.LevelVariants[0]));
                writeValue(lvls);
            }

            if (info.LevelVariants.SelectMany(x => x).Any(x => x != null))
                throw new Exception("The game has level variations!");

            writeValue($"{info.RunningDemo?.Level}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}