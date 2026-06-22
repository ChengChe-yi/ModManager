using System.Collections.Generic;
using System.Threading.Tasks;
using ModManager.Core.Models;

namespace ModManager.Core.Contracts.Services
{
    public interface IModPresetService
    {
        /// <summary>加载角色所有预设</summary>
        Task<List<ModPreset>> LoadPresetsAsync(string characterName);

        /// <summary>保存角色所有预设</summary>
        Task SavePresetsAsync(string characterName, List<ModPreset> presets);

        /// <summary>从 d3dx_user.ini 读取当前值，更新预设变量的 CurrentValue</summary>
        Task ReadCurrentValuesAsync(string characterName, List<ModPreset> presets);

        /// <summary>扫描角色下所有 Mod 的 global persist 变量</summary>
        Task<List<PresetVar>> ScanModVarsAsync(string characterName);

        /// <summary>写入 d3dx_preset.ini</summary>
        Task WritePresetAsync(List<PresetVar> entries);

        /// <summary>构造 DLL 内部键名</summary>
        string BuildGlobalKey(string modsRoot, string iniRelativePath, string varName);
    }
}
