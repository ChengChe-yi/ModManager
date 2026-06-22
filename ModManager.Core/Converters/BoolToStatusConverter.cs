using Microsoft.UI.Xaml.Data;
using System;

namespace ModManager.Core.Converters
{
	public class BoolToStatusConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language) =>
			(value is true) ? "已启用" : "已禁用";
		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotImplementedException();
	}
}
