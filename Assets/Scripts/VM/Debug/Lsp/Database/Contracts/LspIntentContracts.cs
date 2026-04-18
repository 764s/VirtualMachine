// Responsibility:
//   Defines stable user-intent contracts at LSP boundary for anti-drift execution routing.
// Owns:
//   Intent identifiers, protocol method mapping, bridge binding metadata, and coverage checks.
// Inputs/Outputs:
//   In: intent id or protocol method from bridge/runtime.
//   Out: immutable intent contract descriptors for routing/validation.
// Allowed Dependencies:
//   - DatabaseOperationKind
// Forbidden Dependencies:
//   - Concrete protocol transport and JSON-RPC framing.
//   - Feature algorithm implementations.
// Invariants:
//   - Intent codes are unique and stable.
//   - Bridge-routed intents always have bridge binding metadata.
// Boundary Closure:
//   Upstream: LspServerNew and bridge dispatch.
//   Downstream: database operation request/query execution path.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public enum LspIntentExecutionShape
	{
		Unknown = 0,
		Lifecycle,
		DocumentWrite,
		QueryRead,
		Feedback,
		Bootstrap
	}

	public enum LspUserIntentId
	{
		Unknown = 0,

		IntLc01InitializeSession,
		IntLc02Initialized,
		IntLc03Shutdown,
		IntLc04Exit,

		IntDs01DidOpen,
		IntDs02DidChange,
		IntDs03DidClose,
		IntDs04DidChangeWatchedFiles,

		IntQr01DocumentSymbol,
		IntQr02Hover,
		IntQr03Definition,
		IntQr04References,
		IntQr05Completion,
		IntQr06SignatureHelp,
		IntQr07SemanticTokensFull,
		IntQr08PrepareRename,
		IntQr09Rename,
		IntQr10WillRenameFiles,

		IntFb01PublishDiagnostics,
		IntFb02ShowMessageRequest,
		IntFb03ApplyEdit,

		IntBs01PathNormalization,
		IntBs02DiagnosticNormalization,
		IntBs03WorkspaceContextInitialization
	}

	public sealed class LspIntentContract
	{
		public LspUserIntentId IntentId { get; }
		public string IntentCode { get; }
		public string ProtocolMethod { get; }
		public string BridgeMember { get; }
		public LspIntentExecutionShape Shape { get; }
		public DatabaseOperationKind OperationKind { get; }
		public bool RequiresWriteOperation { get; }
		public bool RequiresReadSnapshot { get; }
		public string WriteReason { get; }
		public string QueryOperationName { get; }
		public bool Implemented { get; }

		public LspIntentContract(
			LspUserIntentId intentId,
			string intentCode,
			string protocolMethod,
			string bridgeMember,
			LspIntentExecutionShape shape,
			DatabaseOperationKind operationKind,
			bool requiresWriteOperation,
			bool requiresReadSnapshot,
			string writeReason,
			string queryOperationName,
			bool implemented)
		{
			IntentId = intentId;
			IntentCode = intentCode ?? string.Empty;
			ProtocolMethod = protocolMethod ?? string.Empty;
			BridgeMember = bridgeMember ?? string.Empty;
			Shape = shape;
			OperationKind = operationKind;
			RequiresWriteOperation = requiresWriteOperation;
			RequiresReadSnapshot = requiresReadSnapshot;
			WriteReason = writeReason ?? string.Empty;
			QueryOperationName = queryOperationName ?? string.Empty;
			Implemented = implemented;
		}
	}

	public static class LspIntentContractRegistry
	{
		private static readonly IReadOnlyList<LspIntentContract> Contracts = BuildContracts();
		private static readonly Dictionary<LspUserIntentId, LspIntentContract> ContractsByIntent = BuildContractsByIntent();
		private static readonly Dictionary<string, LspIntentContract> ContractsByMethod = BuildContractsByMethod();

		private static readonly IReadOnlyList<LspUserIntentId> BridgeRoutedIntents = new List<LspUserIntentId>
		{
			LspUserIntentId.IntLc01InitializeSession,
			LspUserIntentId.IntLc02Initialized,
			LspUserIntentId.IntLc03Shutdown,
			LspUserIntentId.IntLc04Exit,
			LspUserIntentId.IntDs01DidOpen,
			LspUserIntentId.IntDs02DidChange,
			LspUserIntentId.IntDs03DidClose,
			LspUserIntentId.IntDs04DidChangeWatchedFiles,
			LspUserIntentId.IntQr01DocumentSymbol,
			LspUserIntentId.IntQr02Hover,
			LspUserIntentId.IntQr03Definition,
			LspUserIntentId.IntQr04References,
			LspUserIntentId.IntQr05Completion,
			LspUserIntentId.IntQr06SignatureHelp,
			LspUserIntentId.IntQr07SemanticTokensFull,
			LspUserIntentId.IntQr08PrepareRename,
			LspUserIntentId.IntQr09Rename,
			LspUserIntentId.IntQr10WillRenameFiles,
		};

		public static IReadOnlyList<LspIntentContract> All => Contracts;

		public static int Count => Contracts.Count;

		public static bool TryGet(LspUserIntentId intentId, out LspIntentContract contract)
		{
			return ContractsByIntent.TryGetValue(intentId, out contract);
		}

		public static LspIntentContract Require(LspUserIntentId intentId)
		{
			if (!TryGet(intentId, out LspIntentContract contract) || contract == null)
				throw new InvalidOperationException("LSP intent contract is not registered: " + intentId + ".");

			return contract;
		}

		public static bool TryGetByMethod(string protocolMethod, out LspIntentContract contract)
		{
			contract = null;
			if (string.IsNullOrWhiteSpace(protocolMethod))
				return false;

			return ContractsByMethod.TryGetValue(protocolMethod, out contract);
		}

		public static bool ValidateBridgeCoverage(out string error)
		{
			error = null;

			int expectedIntentCount = Enum.GetValues(typeof(LspUserIntentId)).Length - 1;
			if (Contracts.Count != expectedIntentCount)
			{
				error = "Intent registry count mismatch. Expected " + expectedIntentCount + ", got " + Contracts.Count + ".";
				return false;
			}

			for (int i = 0; i < BridgeRoutedIntents.Count; i++)
			{
				LspUserIntentId intentId = BridgeRoutedIntents[i];
				if (!TryGet(intentId, out LspIntentContract contract) || contract == null)
				{
					error = "Bridge-routed intent is missing from registry: " + intentId + ".";
					return false;
				}

				if (string.IsNullOrWhiteSpace(contract.BridgeMember))
				{
					error = "Bridge member binding is missing for intent: " + contract.IntentCode + ".";
					return false;
				}

				if (contract.RequiresWriteOperation && contract.OperationKind == DatabaseOperationKind.Unknown)
				{
					error = "Write intent requires explicit operation kind: " + contract.IntentCode + ".";
					return false;
				}

				if (contract.RequiresReadSnapshot && string.IsNullOrWhiteSpace(contract.QueryOperationName))
				{
					error = "Read intent requires query operation name: " + contract.IntentCode + ".";
					return false;
				}
			}

			for (int i = 0; i < Contracts.Count; i++)
			{
				LspIntentContract contract = Contracts[i];
				if (contract == null)
				{
					error = "Intent registry contains null contract at index " + i + ".";
					return false;
				}

				if (!contract.Implemented)
					continue;

				if (!ValidateImplementedContractShape(contract, out error))
					return false;
			}

			return true;
		}

		private static bool ValidateImplementedContractShape(LspIntentContract contract, out string error)
		{
			error = null;
			if (contract == null)
			{
				error = "Implemented intent contract is null.";
				return false;
			}

			if (contract.IntentId == LspUserIntentId.Unknown)
			{
				error = "Implemented intent must not use Unknown id.";
				return false;
			}

			if (string.IsNullOrWhiteSpace(contract.IntentCode))
			{
				error = "Implemented intent has empty intent code: " + contract.IntentId + ".";
				return false;
			}

			if (string.IsNullOrWhiteSpace(contract.ProtocolMethod))
			{
				error = "Implemented intent has empty protocol method: " + contract.IntentCode + ".";
				return false;
			}

			if (string.IsNullOrWhiteSpace(contract.BridgeMember))
			{
				error = "Implemented intent has empty bridge member: " + contract.IntentCode + ".";
				return false;
			}

			if (contract.RequiresWriteOperation)
			{
				if (contract.OperationKind != DatabaseOperationKind.ApplyChangeSet)
				{
					error = "Write intent must use ApplyChangeSet: " + contract.IntentCode + ".";
					return false;
				}

				if (string.IsNullOrWhiteSpace(contract.WriteReason))
				{
					error = "Write intent requires non-empty write reason: " + contract.IntentCode + ".";
					return false;
				}
			}

			if (contract.RequiresReadSnapshot && contract.OperationKind != DatabaseOperationKind.ReadSnapshot)
			{
				error = "Read intent must use ReadSnapshot operation kind: " + contract.IntentCode + ".";
				return false;
			}

			switch (contract.Shape)
			{
				case LspIntentExecutionShape.DocumentWrite:
					if (!contract.RequiresWriteOperation)
					{
						error = "DocumentWrite intent must require write operation: " + contract.IntentCode + ".";
						return false;
					}
					break;

				case LspIntentExecutionShape.QueryRead:
					if (string.IsNullOrWhiteSpace(contract.QueryOperationName))
					{
						error = "QueryRead intent requires query operation name: " + contract.IntentCode + ".";
						return false;
					}

					if (contract.OperationKind == DatabaseOperationKind.Unknown)
					{
						error = "QueryRead intent requires non-unknown operation kind: " + contract.IntentCode + ".";
						return false;
					}
					break;

				case LspIntentExecutionShape.Unknown:
					error = "Implemented intent must not use Unknown execution shape: " + contract.IntentCode + ".";
					return false;
			}

			return true;
		}

		private static IReadOnlyList<LspIntentContract> BuildContracts()
		{
			return new List<LspIntentContract>
			{
				Lifecycle(LspUserIntentId.IntLc01InitializeSession, "INT-LC-01", "initialize", "Initialize", implemented: true),
				Lifecycle(LspUserIntentId.IntLc02Initialized, "INT-LC-02", "initialized", "Initialized", implemented: true),
				Lifecycle(LspUserIntentId.IntLc03Shutdown, "INT-LC-03", "shutdown", "Shutdown", implemented: true),
				Lifecycle(LspUserIntentId.IntLc04Exit, "INT-LC-04", "exit", "Exit", implemented: true),

				DocumentWrite(LspUserIntentId.IntDs01DidOpen, "INT-DS-01", "textDocument/didOpen", "DidOpen", "didOpen", implemented: true),
				DocumentWrite(LspUserIntentId.IntDs02DidChange, "INT-DS-02", "textDocument/didChange", "DidChange", "didChange", implemented: true),
				DocumentWrite(LspUserIntentId.IntDs03DidClose, "INT-DS-03", "textDocument/didClose", "DidClose", "didClose", implemented: true),
				DocumentWrite(LspUserIntentId.IntDs04DidChangeWatchedFiles, "INT-DS-04", "workspace/didChangeWatchedFiles", "DidChangeWatchedFiles", "didChangeWatchedFiles", implemented: true),

				QueryRead(LspUserIntentId.IntQr01DocumentSymbol, "INT-QR-01", "textDocument/documentSymbol", "QueryDocumentSymbol", "documentSymbol", implemented: true),
				QueryRead(LspUserIntentId.IntQr02Hover, "INT-QR-02", "textDocument/hover", "QueryHover", "hover", implemented: true),
				QueryRead(LspUserIntentId.IntQr03Definition, "INT-QR-03", "textDocument/definition", "QueryDefinition", "definition", implemented: true),
				QueryRead(LspUserIntentId.IntQr04References, "INT-QR-04", "textDocument/references", "QueryReferences", "references", implemented: true),
				QueryRead(LspUserIntentId.IntQr05Completion, "INT-QR-05", "textDocument/completion", "QueryCompletion", "completion", implemented: true),
				QueryRead(LspUserIntentId.IntQr06SignatureHelp, "INT-QR-06", "textDocument/signatureHelp", "QuerySignatureHelp", "signatureHelp", implemented: true),
				QueryRead(LspUserIntentId.IntQr07SemanticTokensFull, "INT-QR-07", "textDocument/semanticTokens/full", "QuerySemanticTokensFull", "semanticTokens/full", implemented: true),
				QueryRead(LspUserIntentId.IntQr08PrepareRename, "INT-QR-08", "textDocument/prepareRename", "QueryPrepareRename", "prepareRename", implemented: true),
				QueryRead(LspUserIntentId.IntQr09Rename, "INT-QR-09", "textDocument/rename", "QueryRename", "rename", implemented: true),
				new LspIntentContract(
					LspUserIntentId.IntQr10WillRenameFiles,
					"INT-QR-10",
					"workspace/willRenameFiles",
					"QueryWillRenameFiles",
					LspIntentExecutionShape.QueryRead,
					DatabaseOperationKind.ReadSnapshot,
					requiresWriteOperation: false,
					requiresReadSnapshot: false,
					writeReason: string.Empty,
					queryOperationName: "willRenameFiles",
					implemented: true),

				Feedback(LspUserIntentId.IntFb01PublishDiagnostics, "INT-FB-01", "textDocument/publishDiagnostics", "TryDequeueDiagnostics", implemented: true),
				Feedback(LspUserIntentId.IntFb02ShowMessageRequest, "INT-FB-02", "window/showMessageRequest", "TryDequeueClientRequest", implemented: true),
				Feedback(LspUserIntentId.IntFb03ApplyEdit, "INT-FB-03", "workspace/applyEdit", "HandleClientRequestResponse", implemented: true),

				Bootstrap(LspUserIntentId.IntBs01PathNormalization, "INT-BS-01", "_bootstrap/pathNormalization", "DocumentKeyNormalizer.Normalize", implemented: true),
				Bootstrap(LspUserIntentId.IntBs02DiagnosticNormalization, "INT-BS-02", "_bootstrap/diagnosticNormalization", "NormalizeDiagnostics", implemented: true),
				Bootstrap(LspUserIntentId.IntBs03WorkspaceContextInitialization, "INT-BS-03", "_bootstrap/workspaceContextInit", "Initialize", implemented: true),
			};
		}

		private static Dictionary<LspUserIntentId, LspIntentContract> BuildContractsByIntent()
		{
			var map = new Dictionary<LspUserIntentId, LspIntentContract>();
			for (int i = 0; i < Contracts.Count; i++)
			{
				LspIntentContract contract = Contracts[i];
				if (contract == null)
					continue;

				if (map.ContainsKey(contract.IntentId))
					throw new InvalidOperationException("Duplicate LSP intent registration: " + contract.IntentId + ".");

				map.Add(contract.IntentId, contract);
			}

			return map;
		}

		private static Dictionary<string, LspIntentContract> BuildContractsByMethod()
		{
			var map = new Dictionary<string, LspIntentContract>(StringComparer.Ordinal);
			for (int i = 0; i < Contracts.Count; i++)
			{
				LspIntentContract contract = Contracts[i];
				if (contract == null || string.IsNullOrWhiteSpace(contract.ProtocolMethod))
					continue;

				if (map.ContainsKey(contract.ProtocolMethod))
					throw new InvalidOperationException("Duplicate protocol method registration: " + contract.ProtocolMethod + ".");

				map.Add(contract.ProtocolMethod, contract);
			}

			return map;
		}

		private static LspIntentContract Lifecycle(
			LspUserIntentId intentId,
			string intentCode,
			string protocolMethod,
			string bridgeMember,
			bool implemented)
		{
			return new LspIntentContract(
				intentId,
				intentCode,
				protocolMethod,
				bridgeMember,
				LspIntentExecutionShape.Lifecycle,
				DatabaseOperationKind.Unknown,
				requiresWriteOperation: false,
				requiresReadSnapshot: false,
				writeReason: string.Empty,
				queryOperationName: string.Empty,
				implemented: implemented);
		}

		private static LspIntentContract DocumentWrite(
			LspUserIntentId intentId,
			string intentCode,
			string protocolMethod,
			string bridgeMember,
			string reason,
			bool implemented)
		{
			return new LspIntentContract(
				intentId,
				intentCode,
				protocolMethod,
				bridgeMember,
				LspIntentExecutionShape.DocumentWrite,
				DatabaseOperationKind.ApplyChangeSet,
				requiresWriteOperation: true,
				requiresReadSnapshot: false,
				writeReason: reason,
				queryOperationName: string.Empty,
				implemented: implemented);
		}

		private static LspIntentContract QueryRead(
			LspUserIntentId intentId,
			string intentCode,
			string protocolMethod,
			string bridgeMember,
			string queryOperationName,
			bool implemented)
		{
			return new LspIntentContract(
				intentId,
				intentCode,
				protocolMethod,
				bridgeMember,
				LspIntentExecutionShape.QueryRead,
				DatabaseOperationKind.ReadSnapshot,
				requiresWriteOperation: false,
				requiresReadSnapshot: true,
				writeReason: string.Empty,
				queryOperationName: queryOperationName,
				implemented: implemented);
		}

		private static LspIntentContract Feedback(
			LspUserIntentId intentId,
			string intentCode,
			string protocolMethod,
			string bridgeMember,
			bool implemented)
		{
			return new LspIntentContract(
				intentId,
				intentCode,
				protocolMethod,
				bridgeMember,
				LspIntentExecutionShape.Feedback,
				DatabaseOperationKind.Unknown,
				requiresWriteOperation: false,
				requiresReadSnapshot: false,
				writeReason: string.Empty,
				queryOperationName: string.Empty,
				implemented: implemented);
		}

		private static LspIntentContract Bootstrap(
			LspUserIntentId intentId,
			string intentCode,
			string protocolMethod,
			string bridgeMember,
			bool implemented)
		{
			return new LspIntentContract(
				intentId,
				intentCode,
				protocolMethod,
				bridgeMember,
				LspIntentExecutionShape.Bootstrap,
				DatabaseOperationKind.Unknown,
				requiresWriteOperation: false,
				requiresReadSnapshot: false,
				writeReason: string.Empty,
				queryOperationName: string.Empty,
				implemented: implemented);
		}
	}
}