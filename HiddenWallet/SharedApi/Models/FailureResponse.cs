﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace HiddenWallet.SharedApi.Models
{
	public class FailureResponse : BaseResponse
	{
		public FailureResponse() => Success = false;
		public string Message { get; set; }
		public string Details { get; set; }
	}
}
