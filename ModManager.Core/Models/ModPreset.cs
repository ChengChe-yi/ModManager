using System.Collections.Generic;

namespace ModManager.Core.Models
{
    public class ModPreset
    {
        public string Name { get; set; } = "";
        public string ModFolderName { get; set; } = "";
        public List<PresetVar> Variables { get; set; } = new();
    }
}
