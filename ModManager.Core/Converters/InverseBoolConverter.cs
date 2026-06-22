using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace ModManager.Core.Converters
{
	/// <summary>
	/// 布尔取反并转为 Visibility。
	/// true  → Collapsed（隐藏），false → Visible（显示）。
	/// 适用于 IsEnabled 等正向属性绑定到"禁用指示条"等反向可见性场景。
	/// </summary>
	public class InverseBoolConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language) =>
			(value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			(value is Visibility v && v == Visibility.Collapsed);
	}
}
