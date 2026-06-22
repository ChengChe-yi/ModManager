namespace ModManager.Core.Models
{
    public class PresetVar
    {
        public string VariableName { get; set; } = "";
        public string GlobalKey { get; set; } = "";
        public string ModFolderName { get; set; } = "";
        public double CurrentValue { get; set; }
        public double TargetValue { get; set; }
    }
}
