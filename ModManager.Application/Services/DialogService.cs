using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ModManager.Core.Contracts.Services;

namespace ModManager.Application.Services
{
	public class DialogService : IDialogService
	{
		public async Task ShowInfoAsync(object xamlRootObj, string title, string message, string closeButtonText = "确定")
		{
			if (xamlRootObj is not XamlRoot xamlRoot) return;
			var dialog = new ContentDialog
			{
				XamlRoot = xamlRoot,
				Title = title,
				Content = message,
				CloseButtonText = closeButtonText
			};
			await dialog.ShowAsync();
		}

		public async Task<bool> ShowConfirmAsync(object xamlRootObj, string title, string message, string confirmText = "确定", string cancelText = "取消")
		{
			if (xamlRootObj is not XamlRoot xamlRoot) return false;
			var dialog = new ContentDialog
			{
				XamlRoot = xamlRoot,
				Title = title,
				Content = message,
				PrimaryButtonText = confirmText,
				CloseButtonText = cancelText
			};
			return await dialog.ShowAsync() == ContentDialogResult.Primary;
		}
	}
}