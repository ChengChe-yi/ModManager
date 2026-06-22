using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace ModManager.Core.Converters
{
	public class StatusToBrushConverter : IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, string language)
		{
			bool isEnable = value is bool b && b;
			return new SolidColorBrush(isEnable ? Colors.Green : Colors.Red);
		}
		public object ConvertBack(object value, Type targetType, object parameter, string language) =>
			throw new NotImplementedException();
	}
}
