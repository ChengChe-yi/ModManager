using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ModManager.Core.Models
{
	public class ModItem : INotifyPropertyChanged
	{
		private string _displayName;
		private bool _enable;
		private string _modName = "";
		private string _previewImage;

		public string DisplayName
		{
			get => _displayName;
			set { _displayName = value; OnPropertyChanged(); }
		}

		public bool Enable
		{
			get => _enable;
			set { _enable = value; OnPropertyChanged(); }
		}

		public string ModName
		{
			get => _modName;
			set { _modName = value; OnPropertyChanged(); }
		}

		public string PreviewImage
		{
			get => _previewImage;
			set { _previewImage = value; OnPropertyChanged(); }
		}

		public ObservableCollection<BitmapImage> PreviewImages { get; set; } = new();
		public ObservableCollection<ModKeyBinding> KeyBindings { get; set; } = new();

		public event PropertyChangedEventHandler PropertyChanged;

		protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
		}
	}

	public class ModKeyBinding
	{
		public string KeyName { get; set; } = "";
		public string KeyType { get; set; } = "";
		public string KeyValue { get; set; } = "";
	}
}