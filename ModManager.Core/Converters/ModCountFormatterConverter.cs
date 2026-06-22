using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;

namespace ModManager.Core.Converters
{
	public class ModCountFormatterConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			if (value is int count)
			{
				var suffix = parameter?.ToString() ?? "个";
				return $"{count} {suffix}";
			}
			return "0 个";
		}
		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotImplementedException();
	}
}
