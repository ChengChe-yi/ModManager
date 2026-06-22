using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ModManager.Core.Models
{
    /// <summary>
    /// 角色分类模型，用于 CharacterPage 的角色列表展示。
    /// 每个角色对应 \Mods\Character\ 下的一个子文件夹。
    /// 实现 INotifyPropertyChanged 以支持原地属性更新后 UI 自动刷新。
    /// </summary>
    public class CharacterCategory : INotifyPropertyChanged
    {
        private string _name = "";
        private string _displayName = "";
        private string? _imagePath;
        private bool _isEnabled = true;

        // ===== 卡片展示标签类别 =====
        private static readonly HashSet<string> ElementTags = new(StringComparer.OrdinalIgnoreCase)
            { "火", "水", "风", "雷", "草", "冰", "岩" };

        private static readonly HashSet<string> WeaponTags = new(StringComparer.OrdinalIgnoreCase)
            { "单手剑", "双手剑", "长枪", "弓箭", "弓", "法器", "大剑" };

        private static readonly HashSet<string> BodyTags = new(StringComparer.OrdinalIgnoreCase)
            { "萝莉", "少女", "成女", "少男", "成男" };

        /// <summary>卡片下方展示标签（元素 + 武器 + 体形）</summary>
        public IEnumerable<string> DisplayTags =>
            Tags.Where(t => ElementTags.Contains(t) || WeaponTags.Contains(t) || BodyTags.Contains(t));

        /// <summary>文件夹名（含可能的 DISABLED 前缀）</summary>
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(); } }
        }

        /// <summary>去除 DISABLED 前缀后的显示名称</summary>
        public string DisplayName
        {
            get => _displayName;
            set { if (_displayName != value) { _displayName = value; OnPropertyChanged(); } }
        }

        /// <summary>角色封面图路径（Icon.png）</summary>
        public string? ImagePath
        {
            get => _imagePath;
            set { if (_imagePath != value) { _imagePath = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// 角色标签列表，来源于角色文件夹下的 info.json。
        /// 示例：["雷", "长枪", "女", "少女", "5星", "璃月"]
        /// </summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>该角色下的 Mod 列表</summary>
        public List<ModItem> Mods { get; set; } = new();

        /// <summary>是否启用（未被 DISABLED 前缀禁用）</summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set { if (_isEnabled != value) { _isEnabled = value; OnPropertyChanged(); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
