namespace ModManager.Core.Constants
{
	/// <summary>
	/// 项目中复用的文件名和目录名常量。
	/// </summary>
	public static class FileNames
	{
		// ======== 目录名 ========
		public const string ModsFolder = "Mods";
		public const string CharacterFolder = "Character";
		public const string AssetsFolder = "Assets";
		public const string SettingFolder = "Setting";
		public const string PresetsFolder = "Presets";

		// ======== JSON 数据文件 ========
		public const string CharactersDatabase = "characters.json";
		public const string CharacterInfo = "info.json";

		// ======== 3DMigoto 配置文件 ========
		public const string D3dxUserIni = "d3dx_user.ini";
		public const string D3dxPresetIni = "d3dx_preset.ini";
		public const string IniExtension = "*.ini";

		// ======== 日志文件 ========
		public const string CrashLog = "crash.log";
		public const string AppLogPrefix = "app_";
		public const string AppLogExtension = ".log";

		// ======== 图片扩展名 ========
		public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
	}
}
