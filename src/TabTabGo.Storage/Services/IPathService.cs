using Newtonsoft.Json;
using System.Collections.Generic;


namespace TabTabGo.Storage.Services;

public interface IPathService
{
    string ResolvePath(string path);
    void EnsureDirectory(string path);
    PathSettings PathSettings { get; }
}

public class PathSettings
{
    [JsonExtensionData]
    public IDictionary<string, string> FileTypePaths { get; set; } = new Dictionary<string, string>();

    public string RootPath { get; set; } = string.Empty;
    public string DateFormat { get; set; }

    public string this[string fileTypeName]
    {
        get => FileTypePaths[fileTypeName];
        set
        {
            if (FileTypePaths.ContainsKey(fileTypeName))
                FileTypePaths[fileTypeName] = value;
            else
                FileTypePaths.Add(fileTypeName, value);
        }
    }
}