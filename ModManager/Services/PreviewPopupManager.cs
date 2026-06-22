using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WinUIApplication = Microsoft.UI.Xaml.Application;
namespace ModManager.Services
{
	public class PreviewPopupManager
	{
		private readonly Page _page;
		private Popup? _popup;
		private object? _currentItem;
		private const int MaxImageCacheSize = 20;
		private readonly Dictionary<string, BitmapImage> _imageCache = new();
		private readonly LinkedList<string> _imageCacheOrder = new(); // LRU: front=most recent
		private Point _lastMousePosition;
		private bool _isTracking;

		// 光晕相关
		private FrameworkElement? _glowContainer;
		private Ellipse? _glowEllipse;

		public void ForceHidePreview()
		{
			HidePreviewInternal();
		}
		public PreviewPopupManager(Page page)
		{
			_page = page;
		}


		public void ShowPreview(string imagePath, PointerRoutedEventArgs e, object item)
		{
			if (item == _currentItem) return;

			HidePreviewInternal();
			_currentItem = item;

			var popup = GetOrCreatePopup();
			UpdateImage(imagePath);

			var point = e.GetCurrentPoint(null).Position;
			_lastMousePosition = point;
			PositionPopup(point);
			popup.IsOpen = true;

			StartTracking();
		}


		public void HidePreview(object item)
		{
			if (_currentItem == item)
				HidePreviewInternal();
		}


		public void MovePreview(PointerRoutedEventArgs e)
		{
			_lastMousePosition = e.GetCurrentPoint(null).Position;
		}


		public void Cleanup()
		{
			HidePreviewInternal();
			_imageCache.Clear();
			_imageCacheOrder.Clear();
			HideGlow();
		}



		public void ShowGlow(FrameworkElement element, PointerRoutedEventArgs e)
		{
			// 同一元素仅更新位置（避免闪烁）
			if (_glowContainer == element && _glowEllipse != null)
			{
				UpdateGlowPosition(_glowEllipse, e.GetCurrentPoint(element).Position, element);
				return;
			}
			HideGlowInternal();

			// 缓存查找结果
			if (element.Tag == null)
				element.Tag = FindChildByName<Ellipse>(element, "HoverGlow");

			var glow = element.Tag as Ellipse;
			if (glow == null)
				return;

			_glowContainer = element;
			_glowEllipse = glow;
			glow.Opacity = 1;
			UpdateGlowPosition(glow, e.GetCurrentPoint(element).Position, element);
		}

		public void HideGlow()
		{
			HideGlowInternal();
		}

		private void HideGlowInternal()
		{
			if (_glowEllipse != null)
				_glowEllipse.Opacity = 0;
			_glowContainer = null;
			_glowEllipse = null;
		}

		public void MoveGlow(FrameworkElement element, PointerRoutedEventArgs e)
		{
			if (_glowContainer == element && _glowEllipse != null)
				UpdateGlowPosition(_glowEllipse, e.GetCurrentPoint(element).Position, element);
		}



		private Popup GetOrCreatePopup()
		{
			if (_popup == null)
			{
				_popup = new Popup
				{
					IsLightDismissEnabled = false,
					XamlRoot = _page.XamlRoot,
					Child = new Border
					{
						Background = new AcrylicBrush
						{
							FallbackColor = Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF),
							TintColor = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF),
							TintOpacity = 0.3
						},
						BorderBrush = (Brush)WinUIApplication.Current.Resources["CardStrokeColorDefaultBrush"],
						BorderThickness = new Thickness(1),
						CornerRadius = new CornerRadius(8),
						Padding = new Thickness(8),
						Child = new Image
						{
							Stretch = Stretch.Uniform,
							MaxWidth = 320,
							MaxHeight = 200
						}
					}
				};
			}
			return _popup;
		}

		private void UpdateImage(string? imagePath)
		{
			if (_popup?.Child is Border border && border.Child is Image img)
			{
				img.Source = null;
				if (!string.IsNullOrEmpty(imagePath))
				{
					if (!_imageCache.TryGetValue(imagePath, out var bmp))
					{
						// LRU 淘汰：缓存超限时移除最旧条目
						if (_imageCache.Count >= MaxImageCacheSize
							&& _imageCacheOrder.Last != null)
						{
							var oldest = _imageCacheOrder.Last.Value;
							_imageCache.Remove(oldest);
							_imageCacheOrder.RemoveLast();
						}
						bmp = new BitmapImage(new Uri("file:///" + imagePath.Replace('\\', '/')));
						_imageCache[imagePath] = bmp;
					}
					else
					{
						// 命中时提升到最近使用
						_imageCacheOrder.Remove(imagePath);
					}
					_imageCacheOrder.AddFirst(imagePath);
					img.Source = bmp;
				}
			}
		}

		private void StartTracking()
		{
			if (!_isTracking)
			{
				_page.PointerMoved += OnPointerMoved;
				Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
				_isTracking = true;
			}
		}

		private void StopTracking()
		{
			if (_isTracking)
			{
				_page.PointerMoved -= OnPointerMoved;
				Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
				_isTracking = false;
			}
		}

		private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
		{
			_lastMousePosition = e.GetCurrentPoint(null).Position;
		}

		private void OnRendering(object? sender, object e)
		{
			if (_popup == null || !_popup.IsOpen)
			{
				StopTracking();
				return;
			}
			PositionPopup(_lastMousePosition);
		}

		private double _cachedWindowW, _cachedWindowH;
		private long _cachedSizeTick;

		private void PositionPopup(Point mousePos)
		{
			// 窗口尺寸缓存（只在变化时重新读取）
			var tick = Environment.TickCount64;
			if (tick - _cachedSizeTick > 500)
			{
				_cachedWindowW = _page.ActualWidth;
				_cachedWindowH = _page.ActualHeight;
				_cachedSizeTick = tick;
			}

			double popupWidth = 340;
			double popupHeight = 220;
			double offsetX = 15;
			double offsetY = 15;

			if (mousePos.X + offsetX + popupWidth > _cachedWindowW)
				offsetX = -popupWidth - 15;
			if (mousePos.Y + offsetY + popupHeight > _cachedWindowH)
				offsetY = -popupHeight - 15;

			_popup!.HorizontalOffset = mousePos.X + offsetX;
			_popup.VerticalOffset = mousePos.Y + offsetY;
		}

		private void HidePreviewInternal()
		{
			if (_popup != null && _popup.IsOpen)
			{
				_popup.IsOpen = false;
				if (_popup.Child is Border b && b.Child is Image img)
				{
					img.Source = null;
				}
			}
			StopTracking();
			_currentItem = null;
		}

		private static void UpdateGlowPosition(Ellipse glow, Point pointerPos, FrameworkElement? refElement = null)
		{
			var transform = (TranslateTransform)glow.RenderTransform;
			transform.X = pointerPos.X - glow.Width / 2;
			transform.Y = pointerPos.Y - glow.Height / 2;
		}

		private static T? FindChildByName<T>(FrameworkElement parent, string name) where T : FrameworkElement
		{
			if (parent is Panel panel)
			{
				foreach (var child in panel.Children)
				{
					if (child is T element && element.Name == name)
						return element;
					if (child is FrameworkElement fe)
					{
						var found = FindChildByName<T>(fe, name);
						if (found != null) return found;
					}
				}
			}
			return null;
		}
	}
}