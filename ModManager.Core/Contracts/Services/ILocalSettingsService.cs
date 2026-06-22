using System.Threading.Tasks;

namespace ModManager.Core.Contracts.Services
{
	public interface ILocalSettingsService
	{
		Task InitializeAsync();
		Task<object?> ReadSettingAsync(string key);
		Task SaveSettingAsync<T>(string key, T value);
		Task RemoveSettingAsync(string key);
		Task<T?> ReadSettingAsync<T>(string key);
	}
}