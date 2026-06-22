using System;
using System.Collections.Generic;
using System.Text;

namespace ModManager.Core.Messages
{
	public class MinimizeToTrayChangedMessage(bool value)
	{
		public bool Value { get; } = value;
	}
}
