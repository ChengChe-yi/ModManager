using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModManager.Core.Models
{
	public class ModCategory
	{
		public string Name { get; set; } = "";
		public int Id { get; set; }
		public string DisplayName { get; set; } = "";
		public int ModNumber { get; set; }
		public string? BackgroundImage { get; set; } = null;
		public bool NotEnable { get; set; }  // 用于左侧红色指示条
	}
}
