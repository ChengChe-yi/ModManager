namespace ModManager.Core.Models
{
    /// <summary>预设条目：一个全局键（$\mods\...\varname）及其当前值和变量名。</summary>
    public class ModPresetEntry
    {
        /// <summary>DLL 内部键名，如 $\mods\character\xxx\mod.ini\hair</summary>
        public string GlobalKey { get; set; } = "";

        /// <summary>变量名（不含 $ 前缀），如 hair</summary>
        public string VariableName { get; set; } = "";

        /// <summary>当前值</summary>
        public double Value { get; set; }

        /// <summary>所属 Mod 显示名</summary>
        public string ModDisplayName { get; set; } = "";

        /// <summary>INITIAL 值（来自 mod ini 的 [Constants]）</summary>
        public double InitialValue { get; set; }
    }
}
