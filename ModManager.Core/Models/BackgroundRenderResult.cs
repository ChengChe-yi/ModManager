using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;

namespace ModManager.Core.Models
{
	public class BackgroundRenderResult
	{
		public ImageSource? ImageSource { get; set; }   
		public MediaSource? VideoSource { get; set; }   
		public bool IsVideo { get; set; }
	}
}