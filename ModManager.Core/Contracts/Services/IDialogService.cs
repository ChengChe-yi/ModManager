using System.Threading.Tasks;

namespace ModManager.Core.Contracts.Services
{
	public interface IDialogService
	{
		Task ShowInfoAsync(object xamlRoot, string title, string message, string closeButtonText = "确定");
		Task<bool> ShowConfirmAsync(object xamlRoot, string title, string message, string confirmText = "确定", string cancelText = "取消");
	}
}