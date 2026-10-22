using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using WandEnhancer.Core.Js;
using WandEnhancer.Models;

namespace WandEnhancer.Core
{
    /// <summary>
    /// Anchors patches on stable API names and resolves renamed identifiers within each method.
    /// </summary>
    internal static class EnhancerConfig
    {
        /// <summary>Locates the edits a patch must make, or null when the anchor is absent from this file.</summary>
        public delegate JsEdit[] PatchLocator(JsCursor js);

        public sealed class PatchEntry
        {
            public string Name { get; set; }
            public PatchLocator Locate { get; set; }
            public string[] CandidateFileNames { get; set; }
            public string[] SearchHints { get; set; }

            /// <summary>Marks the patch optional: builds without these strings lack the feature entirely.</summary>
            public string[] CapabilityHints { get; set; }

            public bool Applied { get; set; }
            public bool CapabilityDetected { get; set; }

            public bool IsOptional => CapabilityHints != null && CapabilityHints.Length > 0;

            /// <summary>True once the patch is applied, or once a scan proved the feature is absent.</summary>
            public bool IsResolved => Applied || (IsOptional && !CapabilityDetected);
        }

        public static Dictionary<EPatchType, PatchEntry[]> GetInstance()
        {
            return new Dictionary<EPatchType, PatchEntry[]>
            {
                {
                    EPatchType.ActivatePro,
                    new[]
                    {
                        new PatchEntry
                        {
                            Name = "getUserAccount",
                            SearchHints = new[] { "getUserAccount(" },
                            Locate = js => ForceProSubscription(js, "getUserAccount")
                        },
                        new PatchEntry
                        {
                            Name = "setAccountWandBrandExperience",
                            SearchHints = new[] { "setAccountWandBrandExperience(" },
                            CapabilityHints = new[] { "/v3/account/brand_experience_wand" },
                            Locate = js => ForceProSubscription(js, "setAccountWandBrandExperience")
                        },
                        new PatchEntry
                        {
                            // Language changes replace the account in the store.
                            Name = "setAccountLanguage",
                            SearchHints = new[] { "setAccountLanguage(" },
                            Locate = js => ForceProSubscription(js, "setAccountLanguage")
                        },
                        new PatchEntry
                        {
                            // Covers account writes that bypass the API wrappers.
                            Name = "setAccountReducer",
                            SearchHints = new[] { "ACTION_SET_ACCOUNT" },
                            Locate = LocateAccountReducer
                        },
                        new PatchEntry
                        {
                            // Native phone pairing signs out the patched desktop session.
                            Name = "disableNativeRemotePairing",
                            SearchHints = new[] { "requestRemoteAuthCode" },
                            Locate = js => Edits(js.FindFunction("requestRemoteAuthCode")?
                                .ReplaceBody(PatchPayload.Load("disable-native-pairing")))
                        }
                    }
                },
                {
                    EPatchType.DisableUpdates,
                    new[]
                    {
                        new PatchEntry
                        {
                            Name = "disableUpdateCheck",
                            CandidateFileNames = new[] { "index.js" },
                            SearchHints = new[] { "ACTION_CHECK_FOR_UPDATE" },
                            Locate = LocateUpdateHandler
                        }
                    }
                },
                {
                    EPatchType.DevToolsOnF12,
                    new[]
                    {
                        new PatchEntry
                        {
                            // Electron's main-process API is more stable than the renderer dispatcher.
                            Name = "devToolsBeforeInputEvent",
                            CandidateFileNames = new[] { "index.js" },
                            SearchHints = new[] { "whenReady().then(" },
                            Locate = LocateDevToolsHook
                        }
                    }
                },
                {
                    EPatchType.RemoteWebPanelPreview,
                    new[]
                    {
                        new PatchEntry
                        {
                            Name = "remoteBridgeMainBoot",
                            CandidateFileNames = new[] { "index.js" },
                            SearchHints = new[] { "whenReady().then(run)" },
                            Locate = LocateBridgeBoot
                        },
                        new PatchEntry
                        {
                            Name = "remoteBridgeReset",
                            SearchHints = new[] { "client-state" },
                            Locate = LocateBridgeReset
                        },
                        new PatchEntry
                        {
                            Name = "remoteBridgeSyncSnapshot",
                            SearchHints = new[] { "client-state" },
                            Locate = LocateBridgeSync
                        },
                        new PatchEntry
                        {
                            Name = "remoteBridgeBindHandler",
                            SearchHints = new[] { "setCurrentTrainer(" },
                            Locate = LocateBridgeBindHandler
                        },
                        new PatchEntry
                        {
                            Name = "remoteBridgeValueDelta",
                            SearchHints = new[] { "client-value-changed" },
                            Locate = LocateBridgeValueDelta
                        }
                    }
                }
            };
        }

        /// <summary>Wraps the account-returning promise so the resolved account always reports an active subscription.</summary>
        private static JsEdit[] ForceProSubscription(JsCursor js, string methodName)
        {
            var method = js.FindFunction(methodName);
            return method == null ? null : method.WrapAllReturns(PatchPayload.Load("pro-subscription"));
        }

        private static JsEdit[] LocateAccountReducer(JsCursor js)
        {
            int anchor = js.IndexOf("\"ACTION_SET_ACCOUNT\"");
            var reducer = anchor < 0 ? null : js.FindFunctionAfter(anchor);
            if (reducer == null)
            {
                return null;
            }

            // ${account} is a regex back-reference, not a PatchPayload placeholder.
            return Edits(reducer.ReplaceInBody(
                @"account:\s*(?<account>[\w$]+)",
                PatchPayload.Load("pro-account-reducer")));
        }

        private static JsEdit[] LocateUpdateHandler(JsCursor js)
        {
            int callOpen = js.FindCall("registerHandler", "\"ACTION_CHECK_FOR_UPDATE\"");
            if (callOpen < 0)
            {
                return null;
            }

            return Edits(new JsEdit(callOpen + 1, js.MatchClose(callOpen), PatchPayload.Load("disable-updates")));
        }

        private static JsEdit[] LocateDevToolsHook(JsCursor js)
        {
            var match = WhenReady.Match(js.Text);
            if (!match.Success)
            {
                return null;
            }

            var payload = PatchPayload.Load("devtools-f12", "app", match.Groups["app"].Value);
            return Edits(new JsEdit(match.Index, match.Index, payload));
        }

        private static JsEdit[] LocateBridgeBoot(JsCursor js)
        {
            var match = WhenReadyThenRun.Match(js.Text);
            if (!match.Success)
            {
                return null;
            }

            var payload = PatchPayload.Load("remote-bridge-boot", "app", match.Groups["app"].Value);
            return Edits(new JsEdit(match.Index, match.Index + match.Length, payload));
        }

        /// <summary>Clears the bridge alongside the session fields the reset method already nulls out.</summary>
        private static JsEdit[] LocateBridgeReset(JsCursor js)
        {
            var sync = FindClientStateMethod(js);
            var reset = sync == null ? null : js.FunctionEndingAt(js.SkipWhitespaceBack(sync.Start - 1));
            if (reset == null || reset.Body.IndexOf("Date.now()", StringComparison.Ordinal) < 0)
            {
                return null;
            }

            return Edits(reset.InsertAtEnd(PatchPayload.Load("remote-bridge-reset")));
        }

        /// <summary>Copies Wand's client-state fields without depending on their names or order.</summary>
        private static JsEdit[] LocateBridgeSync(JsCursor js)
        {
            int sendOpen = js.FindCall("send", "\"client-state\"");
            if (sendOpen < 0)
            {
                return null;
            }

            RequireStatementCall(js, sendOpen, "client-state send");

            var method = js.EnclosingFunction(sendOpen);
            int snapshotOpen = FindDirectObjectArgument(js, sendOpen, js.MatchClose(sendOpen));
            int snapshotClose = snapshotOpen < 0 ? -1 : js.MatchClose(snapshotOpen);
            if (method == null || snapshotOpen < 0 || snapshotClose < 0)
            {
                throw new Exception("client-state payload object could not be located");
            }

            // Remove the trailing comma before appending bridge fields.
            string snapshot = js.Text.Substring(snapshotOpen + 1, snapshotClose - snapshotOpen - 1)
                .Trim()
                .TrimEnd(',');
            if (snapshot.Length == 0)
            {
                throw new Exception("client-state payload object is empty");
            }

            var payload = PatchPayload.Load(
                "remote-bridge-sync",
                "snapshot", snapshot,
                "trainer", method.Resolve(@"this\.(?<trainer>#[\w$]+)\s*\?\.\s*getMetadata", "trainer"),
                "metadata", method.Resolve(@"getMetadata\(\s*(?<metadata>[\w$]+\.[\w$]+)\s*\)", "metadata"));

            var edits = new List<JsEdit> { new JsEdit(js.MatchClose(sendOpen) + 1, payload) };
            edits.AddRange(HoistConnectedGuard(js, sendOpen));
            return edits.ToArray();
        }

        /// <summary>
        /// Moves the connection guard onto Wand's send so local bridge snapshots remain unconditional.
        /// </summary>
        private static IEnumerable<JsEdit> HoistConnectedGuard(JsCursor js, int sendOpen)
        {
            int blockOpen = js.EnclosingOpener(sendOpen, '{');
            int closeParen = blockOpen < 0 ? -1 : js.SkipWhitespaceBack(blockOpen - 1);
            if (closeParen < 0 || js.Text[closeParen] != ')')
            {
                yield break;
            }

            var stack = js.OpenerStack(closeParen);
            if (stack.Count == 0 || js.NameBefore(stack[0]) != "if")
            {
                yield break;
            }

            int openParen = stack[0];
            string test = js.Text.Substring(openParen + 1, closeParen - openParen - 1);

            // Preserve unrelated conditions and any else branch.
            if (test.IndexOf("this.status", StringComparison.Ordinal) < 0 || HasElseBranch(js, blockOpen))
            {
                yield break;
            }

            int guardStart = js.SkipWhitespaceBack(openParen - 1) - 1;

            int calleeStart = sendOpen;
            while (calleeStart > 0 && IsCalleeChar(js.Text[calleeStart - 1]))
            {
                calleeStart--;
            }

            yield return new JsEdit(calleeStart, calleeStart, $"({test})&&");
            yield return new JsEdit(guardStart, blockOpen, string.Empty);
        }

        private static bool HasElseBranch(JsCursor js, int blockOpen)
        {
            int afterBlock = js.SkipWhitespaceForward(js.MatchClose(blockOpen) + 1);
            return string.CompareOrdinal(js.Text, afterBlock, "else", 0, 4) == 0;
        }

        /// <summary>
        /// The sync call is appended right after the send, so the send must be a
        /// statement-level expression. A send nested in another call's arguments (or
        /// used as a return value) would silently change meaning after the splice.
        /// </summary>
        private static void RequireStatementCall(JsCursor js, int callOpen, string what)
        {
            var stack = js.OpenerStack(callOpen);
            if (stack.Count > 0 && js.Text[stack[0]] != '{')
            {
                throw new Exception($"{what} is nested inside another expression; refusing to splice after it");
            }

            int calleeStart = callOpen;
            while (calleeStart > 0 && IsCalleeChar(js.Text[calleeStart - 1]))
            {
                calleeStart--;
            }

            int before = js.SkipWhitespaceBack(calleeStart - 1);
            if (before < 0)
            {
                return;
            }

            char preceding = js.Text[before];
            if (preceding == '(' || preceding == '[' || preceding == ':' || preceding == '?' || preceding == '>')
            {
                throw new Exception($"{what} is nested inside another expression; refusing to splice after it");
            }

            if (preceding == '_' || preceding == '$' || char.IsLetter(preceding))
            {
                int wordEnd = before;
                while (before > 0 && IsWordChar(js.Text[before - 1]))
                {
                    before--;
                }

                string word = js.Text.Substring(before, wordEnd - before + 1);
                if (word == "return" || word == "case")
                {
                    throw new Exception($"{what} is a {word} value; refusing to splice after it");
                }
            }
        }

        /// <summary>First `{` that is a direct argument of the call, skipping braces nested in earlier arguments.</summary>
        private static int FindDirectObjectArgument(JsCursor js, int callOpen, int callClose)
        {
            if (callClose < 0)
            {
                return -1;
            }

            for (int brace = js.IndexOf("{", callOpen); brace >= 0 && brace < callClose; brace = js.IndexOf("{", brace + 1))
            {
                int before = js.SkipWhitespaceBack(brace - 1);
                if (before >= 0 && js.Text[before] == ',' && js.EnclosingOpener(brace, '(') == callOpen)
                {
                    return brace;
                }
            }

            return -1;
        }

        private static bool IsWordChar(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_' || value == '$';
        }

        private static JsEdit[] LocateBridgeBindHandler(JsCursor js)
        {
            var method = js.FindFunction("setCurrentTrainer");
            if (method == null)
            {
                return null;
            }

            var receiveOpen = js.FindCall("listen", "\"client-value-changed\"");
            var receive = receiveOpen < 0 ? null : js.EnclosingFunction(receiveOpen);
            if (receive == null)
            {
                throw new Exception("Remote value listener could not be located");
            }

            string handler = receive.Resolve(
                @"listen\(\s*""client-value-changed""\s*,\s*\(?\s*[\w$]+\s*\)?\s*=>\s*this\.(?<handler>#[\w$]+)\(",
                "handler");
            var handlerMethod = js.FindFunction(handler);
            if (handlerMethod == null)
            {
                throw new Exception("Remote value handler could not be located");
            }

            SetValueCall setValue = ResolveRemoteSetValue(handlerMethod);
            var parameter = MatchExactlyOnce(
                new Regex(@"setCurrentTrainer\(\s*(?<info>[\w$]+)"),
                js.Text.Substring(method.Start, method.BodyOpen - method.Start),
                "Trainer info parameter");

            return Edits(method.InsertAtStart(PatchPayload.Load(
                "remote-bridge-renderer",
                "trainer", setValue.Trainer,
                "trainerInfo", parameter.Groups["info"].Value,
                "remoteSource", setValue.Source)));
        }

        private static JsEdit[] LocateBridgeValueDelta(JsCursor js)
        {
            int sendOpen = js.FindCall("send", "\"client-value-changed\"");
            if (sendOpen < 0)
            {
                return null;
            }

            var subscription = js.EnclosingFunction(sendOpen);
            var snapshot = FindClientStateMethod(js);
            if (subscription == null || snapshot == null ||
                subscription.Body.IndexOf(".onValueSet(", StringComparison.Ordinal) < 0)
            {
                throw new Exception("Trainer value subscription could not be located");
            }

            // The trainer id is read from the snapshot method but injected into the
            // subscription method; both must live in the same class for `this.#x` to resolve.
            if (js.EnclosingOpener(subscription.Start, '{') != js.EnclosingOpener(snapshot.Start, '{'))
            {
                throw new Exception("Trainer value subscription lives in a different class than the client-state snapshot");
            }

            // Capture at subscription time so a late callback cannot impersonate the next trainer.
            string trainerId = snapshot.Resolve(@"trainerId:\s*(?<id>this\.#[\w$]+)", "id");
            string change = subscription.Resolve(@"name:\s*(?<change>[\w$]+)\.name", "change");
            var callback = Regex.Match(subscription.Body,
                @"\.onValueSet\(\s*\(?\s*" + Regex.Escape(change) + @"\s*\)?\s*=>\s*\{");
            if (!callback.Success)
            {
                throw new Exception("Trainer value callback could not be located");
            }

            return new[]
            {
                subscription.InsertAtStart(PatchPayload.Load("remote-bridge-value-subscription", "trainerId", trainerId)),
                new JsEdit(subscription.BodyOpen + 1 + callback.Index + callback.Length,
                    PatchPayload.Load("remote-bridge-value-delta", "change", change))
            };
        }

        private static JsFunction FindClientStateMethod(JsCursor js)
        {
            int sendOpen = js.FindCall("send", "\"client-state\"");
            return sendOpen < 0 ? null : js.EnclosingFunction(sendOpen);
        }

        private static JsEdit[] Edits(JsEdit edit)
        {
            return edit == null ? null : new[] { edit };
        }

        private static bool IsCalleeChar(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_' || value == '$' || value == '#'
                   || value == '.' || value == '?';
        }

        /// <summary>Match that must be unambiguous: zero or several hits mean an unsupported build.</summary>
        private static Match MatchExactlyOnce(Regex pattern, string text, string what)
        {
            var match = pattern.Match(text);
            if (!match.Success)
            {
                throw new Exception($"{what} could not be located");
            }

            if (match.NextMatch().Success)
            {
                throw new Exception($"{what} matched more than once; cannot tell which call site is the right one");
            }

            return match;
        }

        private static readonly Regex WhenReady = new Regex(@"(?<app>[\w$]+)\.whenReady\(\)\.then\(");
        private static readonly Regex WhenReadyThenRun = new Regex(@"(?<app>[\w$]+)\.whenReady\(\)\.then\(run\)");
        private sealed class SetValueCall
        {
            public string Trainer;
            public string Source;
        }

        /// <summary>
        /// Parses the handler's <c>setValue(name, value, source, ...)</c> call with balanced
        /// delimiters, so a source expression containing commas (or nested calls) is kept
        /// whole. Matching up to the next comma would silently truncate it.
        /// </summary>
        private static SetValueCall ResolveRemoteSetValue(JsFunction handlerMethod)
        {
            var body = new JsCursor(handlerMethod.Body);
            SetValueCall found = null;
            foreach (Match head in RemoteSetValueHead.Matches(handlerMethod.Body))
            {
                SetValueCall candidate = TryParseSetValue(body, head);
                if (candidate == null)
                {
                    continue;
                }

                if (found != null)
                {
                    throw new Exception("Remote setValue call matched more than once; cannot tell which call site is the right one");
                }

                found = candidate;
            }

            if (found == null)
            {
                throw new Exception("Remote setValue call could not be located");
            }

            return found;
        }

        private static SetValueCall TryParseSetValue(JsCursor body, Match head)
        {
            int callOpen = head.Index + head.Length - 1;
            int callClose = body.MatchClose(callOpen);
            if (callClose < 0)
            {
                return null;
            }

            string[] args = SplitTopLevel(body.Text.Substring(callOpen + 1, callClose - callOpen - 1));
            if (args.Length < 3)
            {
                return null;
            }

            var name = Regex.Match(args[0].Trim(), @"^(?<change>[\w$]+)\.name$");
            var value = Regex.Match(args[1].Trim(), @"^(?<change>[\w$]+)\.value$");
            if (!name.Success || !value.Success || name.Groups["change"].Value != value.Groups["change"].Value)
            {
                return null;
            }

            return new SetValueCall { Trainer = head.Groups["trainer"].Value, Source = args[2].Trim() };
        }

        /// <summary>Splits call arguments on commas that are not nested in delimiters, strings, or comments.</summary>
        private static string[] SplitTopLevel(string args)
        {
            var cursor = new JsCursor(args);
            var parts = new List<string>();
            int start = 0;
            for (int index = 0; index < args.Length; index++)
            {
                char current = args[index];
                if (current == '"' || current == '\'' || current == '`')
                {
                    index = SkipQuoted(args, index);
                    continue;
                }

                if (current == '/' && index + 1 < args.Length && (args[index + 1] == '/' || args[index + 1] == '*'))
                {
                    index = SkipComment(args, index);
                    continue;
                }

                if (current == ',' && cursor.OpenerStack(index).Count == 0)
                {
                    parts.Add(args.Substring(start, index - start));
                    start = index + 1;
                }
            }

            parts.Add(args.Substring(start));
            return parts.ToArray();
        }

        private static int SkipQuoted(string text, int index)
        {
            char quote = text[index];
            for (int cursor = index + 1; cursor < text.Length; cursor++)
            {
                if (text[cursor] == '\\')
                {
                    cursor++;
                }
                else if (text[cursor] == quote)
                {
                    return cursor;
                }
            }

            return text.Length;
        }

        private static int SkipComment(string text, int index)
        {
            if (text[index + 1] == '/')
            {
                int lineEnd = text.IndexOf('\n', index);
                return lineEnd < 0 ? text.Length : lineEnd;
            }

            int commentEnd = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
            return commentEnd < 0 ? text.Length : commentEnd + 1;
        }

        private static readonly Regex RemoteSetValueHead =
            new Regex(@"this\.(?<trainer>#[\w$]+)\.setValue\s*\(");
    }
}
