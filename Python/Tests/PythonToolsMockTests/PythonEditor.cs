// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

extern alias analysis;
extern alias pythontools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using pythontools::Microsoft.PythonTools;
using pythontools::Microsoft.PythonTools.Editor;
using Microsoft.PythonTools.Infrastructure;
using pythontools::Microsoft.PythonTools.Intellisense;
using analysis::Microsoft.PythonTools.Interpreter;
using pythontools::Microsoft.PythonTools.Options;
using analysis::Microsoft.PythonTools.Parsing;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudioTools.MockVsTests;
using TestUtilities;

namespace PythonToolsMockTests {
    sealed class PythonEditor : IDisposable {
        private readonly bool _disposeVS, _disposeFactory, _disposeAnalyzer;
        public readonly MockVs VS;
        public readonly IPythonInterpreterFactory Factory;
        public readonly VsProjectAnalyzer Analyzer;
        public readonly MockVsTextView View;
        public readonly AdvancedEditorOptions AdvancedOptions;

        public PythonEditor(
            string content = null,
            PythonLanguageVersion version = PythonLanguageVersion.V27,
            MockVs vs = null,
            IPythonInterpreterFactory factory = null,
            VsProjectAnalyzer analyzer = null,
            string filename = null,
            bool? inProcAnalyzer = null
        ) {
            if (vs == null) {
                _disposeVS = true;
                vs = new MockVs();
            }
            MockVsTextView view = null;
            try {
                AdvancedEditorOptions advancedOptions = null;
                vs.InvokeSync(() => {
                    advancedOptions = vs.GetPyService().AdvancedOptions;
                    advancedOptions.AutoListMembers = true;
                    advancedOptions.AutoListIdentifiers = false;
                });
                AdvancedOptions = advancedOptions;

                if (factory == null) {
                    vs.InvokeSync(() => {
                        factory = vs.ComponentModel.GetService<IInterpreterRegistryService>()
                            .Interpreters
                            .FirstOrDefault(c => c.GetLanguageVersion() == version && c.Configuration.Id.StartsWith("Global|PythonCore"));
                        if (factory != null) {
                            Console.WriteLine($"Using interpreter {factory.Configuration.InterpreterPath}");
                        }
                    });
                    if (factory == null) {
                        _disposeFactory = true;
                        factory = InterpreterFactoryCreator.CreateAnalysisInterpreterFactory(version.ToVersion());
                        Console.WriteLine("Using analysis-only interpreter");
                    }
                }
                if (analyzer == null) {
                    _disposeAnalyzer = true;
                    analyzer = vs.InvokeTask(() => VsProjectAnalyzer.CreateForTestsAsync(vs.ComponentModel.GetService<PythonEditorServices>(), factory, inProcAnalyzer ?? Debugger.IsAttached), 10000);
                }
                if (string.IsNullOrEmpty(filename)) {
                    do {
                        filename = PathUtils.GetAbsoluteFilePath(TestData.GetTempPath(), Path.GetRandomFileName()) + ".py";
                    } while (File.Exists(filename));
                } else if (!Path.IsPathRooted(filename)) {
                    filename = PathUtils.GetAbsoluteFilePath(TestData.GetTempPath(), filename);
                }

                var cancel = CancellationTokens.After60s;
                view = vs.CreateTextView(PythonCoreConstants.ContentType, content ?? "",
                    v => {
                        v.TextView.TextBuffer.Properties[BufferParser.ParseImmediately] = true;
                        v.TextView.TextBuffer.Properties[IntellisenseController.SuppressErrorLists] = IntellisenseController.SuppressErrorLists;
                        v.TextView.TextBuffer.Properties[VsProjectAnalyzer._testAnalyzer] = analyzer;
                        v.TextView.TextBuffer.Properties[VsProjectAnalyzer._testFilename] = filename;
                    },
                    filename);

                var services = vs.ComponentModel.GetService<PythonEditorServices>();
                var bi = services.GetBufferInfo(view.TextView.TextBuffer);
                var entry = bi.GetAnalysisEntryAsync(cancel).WaitAndUnwrapExceptions();
                Assert.IsNotNull(entry, "failed to get analysis entry");

                if (!string.IsNullOrEmpty(content) && !cancel.IsCancellationRequested && !entry.IsAnalyzed) {
                    using (var mre = new ManualResetEventSlim()) {
                        EventHandler evt = (s, e) => mre.SetIfNotDisposed();

                        try {
                            entry.AnalysisComplete += evt;
                            entry.TryGetBufferParser()?.EnsureCodeSyncedAsync(bi.Buffer, true).WaitAndUnwrapExceptions();
                            bool hasStarted = false;
                            while (!mre.Wait(50, cancel) && !vs.HasPendingException && !entry.IsAnalyzed) {
                                if (!hasStarted) {
                                    hasStarted = analyzer.IsAnalyzing;
                                } else if (!analyzer.IsAnalyzing && !entry.IsAnalyzed) {
                                    Assert.Fail("analyzer is not analyzing");
                                }
                            }
                        } catch (OperationCanceledException) {
                        } finally {
                            entry.AnalysisComplete -= evt;
                        }
                    }
                }
                if (cancel.IsCancellationRequested) {
                    Assert.Fail("Timed out waiting for code analysis");
                }

                vs.ThrowPendingException();

                View = view;
                view = null;
                Analyzer = analyzer;
                analyzer = null;
                Factory = factory;
                factory = null;
                VS = vs;
                vs = null;
            } finally {
                if (view != null) {
                    view.Dispose();
                }
                if (analyzer != null && _disposeAnalyzer) {
                    analyzer.Dispose();
                }
                if (factory != null && _disposeFactory) {
                    var disp = factory as IDisposable;
                    if (disp != null) {
                        disp.Dispose();
                    }
                }
                if (vs != null && _disposeVS) {
                    vs.Dispose();
                }
            }
        }

        public string Text {
            get { return View.Text; }
            set {
                var buffer = View.TextView.TextBuffer;
                var bi = PythonTextBufferInfo.TryGetForBuffer(buffer);

                if (bi?.AnalysisEntry == null) {
                    // No analysis yet, so just set the text.
                    using (var edit = View.TextView.TextBuffer.CreateEdit()) {
                        edit.Delete(0, edit.Snapshot.Length);
                        edit.Insert(0, value);
                        edit.Apply();
                    }
                    return;
                }

                using (var edit = buffer.CreateEdit()) {
                    edit.Delete(0, edit.Snapshot.Length);
                    edit.Apply();
                }

                if (string.IsNullOrEmpty(value)) {
                    return;
                }

                using (ManualResetEventSlim mre1 = new ManualResetEventSlim(), mre2 = new ManualResetEventSlim()) {
                    EventHandler evt1 = (s, e) => mre1.SetIfNotDisposed();
                    EventHandler evt2 = (s, e) => mre2.SetIfNotDisposed();
                    Analyzer.AnalysisStarted += evt1;
                    bi.AnalysisEntry.AnalysisComplete += evt2;

                    try {
                        using (var edit = View.TextView.TextBuffer.CreateEdit()) {
                            edit.Insert(0, value);
                            edit.Apply();
                        }

                        if (!mre1.Wait(0)) {
                            mre2.Reset();
                            if (!mre1.Wait(10000)) {
                                throw new TimeoutException("Failed to see buffer start analyzer");
                            }
                        }

                        if (!mre2.Wait(10000)) {
                            throw new TimeoutException("Failed to see entry finish analyzing");
                        }
                    } finally {
                        Analyzer.AnalysisStarted -= evt1;
                        bi.AnalysisEntry.AnalysisComplete -= evt2;
                    }
                }
            }
        }

        public ITextSnapshot CurrentSnapshot {
            get { return View.TextView.TextSnapshot; }
        }

        public WaitHandle AnalysisCompleteEvent {
            get {
                var evt = new AnalysisCompleteManualResetEvent(BufferInfo);
                BufferInfo.AddSink(evt, evt);
                return evt;
            }
        }

        class AnalysisCompleteManualResetEvent : WaitHandle, IPythonTextBufferInfoEventSink {
            private readonly ManualResetEvent _event;
            private readonly PythonTextBufferInfo _info;

            public AnalysisCompleteManualResetEvent(PythonTextBufferInfo info) {
                _event = new ManualResetEvent(false);
                _info = info;
                SafeWaitHandle = _event.SafeWaitHandle;
            }

            protected override void Dispose(bool explicitDisposing) {
                SafeWaitHandle = null;
                base.Dispose(explicitDisposing);
                _event.Dispose();
                if (explicitDisposing) {
                    _info.RemoveSink(this);
                }
            }

            public async Task PythonTextBufferEventAsync(PythonTextBufferInfo sender, PythonTextBufferInfoEventArgs e) {
                if (e.Event == PythonTextBufferInfoEvents.NewAnalysis) {
                    var snapshot = _info.LastAnalysisSnapshot;
                    for (int retries = 100; retries > 0 && (snapshot = _info.LastAnalysisSnapshot) == null; --retries) {
                        await Task.Delay(100);
                    }
                    if (snapshot == null) {
                        throw new NullReferenceException("LastAnalysisSnapshot was not set");
                    }
                    if (snapshot.Version.VersionNumber >= _info.Buffer.CurrentSnapshot.Version.VersionNumber) {
                        try {
                            _event.Set();
                        } catch (ObjectDisposedException) {
                        }
                        _info.RemoveSink(this);
                    }
                }
            }
        }

        internal PythonEditorServices EditorServices => VS.ComponentModel.GetService<PythonEditorServices>();

        internal PythonTextBufferInfo BufferInfo => EditorServices.GetBufferInfo(View.TextView.TextBuffer);

        public List<Completion> GetCompletionListAfter(string substring, bool assertIfNoCompletions = true) {
            var snapshot = CurrentSnapshot;
            return GetCompletionList(snapshot.GetText().IndexOfEnd(substring), assertIfNoCompletions, snapshot);
        }

        public List<Completion> GetCompletionList(
            int index,
            bool assertIfNoCompletions = true,
            ITextSnapshot snapshot = null
        ) {
            for (int retries = 5; retries > 0; --retries) {
                var completions = GetCompletionListNoRetry(index, false, snapshot);
                if (completions.Any()) {
                    return completions;
                }
            }
            if (assertIfNoCompletions) {
                Assert.Fail("No completions");
            }
            return new List<Completion>();
        }

        public List<Completion> GetCompletionListNoRetry(
            int index,
            bool assertIfNoCompletions = true,
            ITextSnapshot snapshot = null
        ) {
            snapshot = snapshot ?? CurrentSnapshot;
            if (index < 0) {
                index += snapshot.Length + 1;
            }
            View.MoveCaret(new SnapshotPoint(snapshot, index));
            VS.Invoke(() => View.MemberList());
            using (var sh = View.WaitForSession<ICompletionSession>(assertIfNoSession: assertIfNoCompletions)) {
                if (sh == null) {
                    return new List<Completion>();
                }
                Assert.AreNotEqual(0, sh.Session.CompletionSets.Count);
                return sh.Session.CompletionSets.SelectMany(cs => cs.Completions)
                    .Where(c => !string.IsNullOrEmpty(c?.InsertionText))
                    .ToList();
            }
        }

        public void Backspace() => VS.InvokeSync(() => View.Backspace());
        public void Enter() => VS.InvokeSync(() => View.Enter());
        public void Clear() => VS.InvokeSync(() => View.Clear());
        public void MoveCaret(int line, int column) => View.MoveCaret(line, column);
        public void MemberList() => VS.InvokeSync(() => View.MemberList());
        public void ParamInfo() => VS.InvokeSync(() => View.ParamInfo());
        public void Type(string text) => VS.InvokeSync(() => View.Type(text));

        public void TypeAndWaitForAnalysis(string text) {
            using (var mre = new ManualResetEventSlim()) {
                EventHandler evt = (s, e) => mre.SetIfNotDisposed();
                Analyzer.AnalysisStarted += evt;

                Type(text);

                var cts = CancellationTokens.After60s;
                try {
                    while (!mre.Wait(500, cts) && !VS.HasPendingException) { }
                    Analyzer.WaitForCompleteAnalysis(x => !cts.IsCancellationRequested && !VS.HasPendingException);
                } catch (OperationCanceledException) {
                } finally {
                    Analyzer.AnalysisStarted -= evt;
                }
                if (cts.IsCancellationRequested) {
                    Assert.Fail("Timed out waiting for code analysis");
                }
                VS.ThrowPendingException();
            }
        }

        public IEnumerable<string> GetCompletions(int index) {
            return GetCompletionList(index, false).Select(c => c.DisplayText);
        }

        public IEnumerable<string> GetCompletionsAfter(string substring) {
            return GetCompletionListAfter(substring, false).Select(c => c.DisplayText);
        }

        public object GetAnalysisEntry(ITextBuffer buffer = null) {
            return EditorServices.GetBufferInfo(buffer ?? View.TextView.TextBuffer).AnalysisEntry
                ?? throw new ArgumentException("no AnalysisEntry available");
        }

        public void Dispose() {
            if (View != null) {
                View.Dispose();
            }
            if (Analyzer != null && _disposeAnalyzer) {
                Analyzer.Dispose();
            }
            if (Factory != null && _disposeFactory) {
                var disp = Factory as IDisposable;
                if (disp != null) {
                    disp.Dispose();
                }
            }
            if (VS != null && _disposeVS) {
                VS.Dispose();
            }
        }
    }
}
