using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace ModManager.Core.Contracts.Services
{
	public interface IThemeSelectorService
	{
		Task InitializeAsync();
		Task SetThemeAsync(ElementTheme theme);
		Task SetRequestedThemeAsync(FrameworkElement rootElement); 
	}
}