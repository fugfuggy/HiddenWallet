﻿using HiddenWallet.Daemon.Models;
using HiddenWallet.FullSpvWallet.ChaumianCoinJoin;
using HiddenWallet.KeyManagement;
using HiddenWallet.Models;
using HiddenWallet.SharedApi.Models;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HiddenWallet.Daemon.Controllers
{
	[Route("api/v1/[controller]")]
	public class TumblerController : Controller
    {
		[HttpGet]
		public string Test()
		{
			return "test";
		}

		[Route("connection")]
		[HttpGet]
		public async Task<IActionResult> ConnectionAsync()
		{ 
			try
			{
				CoinJoinService coinJoinService = Global.WalletWrapper.WalletJob.CoinJoinService;
				if (coinJoinService.TumblerConnection == null)
				{
					await coinJoinService.SubscribeNotificationsAsync();
				}
				if (coinJoinService.TumblerConnection == null)
				{
					return new ObjectResult(new FailureResponse { Message = "", Details = "" });
				}
				else
				{
					if(coinJoinService.StatusResponse == null)
					{
						coinJoinService.StatusResponse = await coinJoinService.TumblerClient.GetStatusAsync(CancellationToken.None);
					}

					return new ObjectResult(new SuccessResponse());
				}
			}
			catch (Exception ex)
			{
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = ex.ToString() });
			}
		}

		[Route("ongoing-mix")]
		[HttpGet]
		public IActionResult OngoingMix()
		{
			try
			{
				if (IsMixOngoing)
				{
					return new ObjectResult(new YesNoResponse { Value = true });
				}
				else
				{
					return new ObjectResult(new YesNoResponse { Value = false });
				}
			}
			catch (Exception ex)
			{
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = ex.ToString() });
			}
		}

		// asp.net core brainfart, must keep these static
		private static bool IsMixOngoing { get; set; }
		private static CancellationTokenSource CancelMixSource { get; set; } = null;
		[Route("tumble")]
		[HttpPost]
		public async Task<IActionResult> TumbleAsync([FromBody]TumbleRequest request)
		{
			List<uint256> txIds = new List<uint256>();
			IsMixOngoing = true;
			try
			{
				if (request == null || request.From == null || request.To == null || request.RoundCount == 0)
				{
					return new ObjectResult(new FailureResponse { Message = "Bad request", Details = "" });
				}

				var getFrom = Global.WalletWrapper.GetAccount(request.From, out SafeAccount fromAccount);
				if (getFrom != null) return new ObjectResult(getFrom);

				var getTo = Global.WalletWrapper.GetAccount(request.To, out SafeAccount toAccount);
				if (getTo != null) return new ObjectResult(getTo);

				CancelMixSource = new CancellationTokenSource();

				for (int i = 0; i < request.RoundCount; i++)
				{
					IEnumerable<Script> unusedOutputs = await Global.WalletWrapper.WalletJob.GetUnusedScriptPubKeysAsync(AddressType.Pay2WitnessPublicKeyHash, toAccount, HdPathType.NonHardened);
					BitcoinAddress activeOutput = unusedOutputs.RandomElement().GetDestinationAddress(Global.WalletWrapper.WalletJob.CurrentNetwork); // TODO: this is sub-optimal, it'd be better to not which had been already registered and not reregister it
					BitcoinWitPubKeyAddress bech32 = new BitcoinWitPubKeyAddress(activeOutput.ToString(), Global.WalletWrapper.WalletJob.CurrentNetwork);

					uint256 txid = null;
					try
					{
						txid = await Global.WalletWrapper.WalletJob.CoinJoinService.TumbleAsync(fromAccount, bech32, CancelMixSource.Token);
					}
					catch (InvalidOperationException e) when (e.Message == "Round aborted")
					{
						// register for the next round
						i--;
						continue;
					}

					if (txid == null)
					{
						return new ObjectResult(new FailureResponse { Message = "Either the coordinator failed to propagate the latest transaction or it did not arrive to our mempool", Details = "Successful mixes:" + Environment.NewLine + string.Join(Environment.NewLine, txIds.Select(a => a.ToString())) });
					}
					txIds.Add(txid);
					if (CancelMixSource.Token.IsCancellationRequested)
					{
						string details = "";
						if(txIds.Count > 0)
						{
							details = "Successful mixes:" + Environment.NewLine + string.Join(Environment.NewLine, txIds.Select(a => a.ToString()));
						}
						return new ObjectResult(new FailureResponse { Message = "Mixing was cancelled", Details = details });
					}
				}

				return new ObjectResult(new TumbleResponse() { Transactions = string.Join(Environment.NewLine, txIds.Select(a => a.ToString())) });
			}
			catch (OperationCanceledException)
			{
				string details = "";
				if (txIds.Count > 0)
				{
					details = "Successful mixes:" + Environment.NewLine + string.Join(Environment.NewLine, txIds.Select(a => a.ToString()));
				}
				return new ObjectResult(new FailureResponse { Message = "Mixing was cancelled", Details = details });
			}
			catch (Exception ex)
			{
				string details = "";
				if (txIds.Count > 0)
				{
					details = "Successful mixes:" + Environment.NewLine + string.Join(Environment.NewLine, txIds.Select(a => a.ToString()));
				}
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = details });
			}
			finally
			{
				CancelMixSource?.Dispose();
				IsMixOngoing = false;
			}
		}

		[Route("cancel-mix")]
		[HttpGet]
		public async Task<IActionResult> CancelMixAsync()
		{
			try
			{
				CancelMixSource?.Cancel();
				while(IsMixOngoing)
				{
					await Task.Delay(100);
				}
				return new ObjectResult(new SuccessResponse());
			}
			catch (Exception ex)
			{
				return new ObjectResult(new FailureResponse { Message = ex.Message, Details = ex.ToString() });
			}
		}
	}
}
