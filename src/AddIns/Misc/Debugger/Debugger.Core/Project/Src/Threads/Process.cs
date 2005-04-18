﻿// <file>
//     <owner name="David Srbecký" email="dsrbecky@post.cz"/>
// </file>

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

using DebuggerInterop.Core;
using DebuggerInterop.MetaData;

namespace DebuggerLibrary
{
	public class Process 
	{
		ICorDebugProcess           corProcess;

		Thread                     mainThread;
		Thread                     currentThread;
		bool                       isProcessRunning;

		internal Process(ICorDebugProcess corProcess)
		{
			this.corProcess = corProcess;
		}

		internal ICorDebugProcess CorProcess {
			get {
				return corProcess;
			}
		}

		public Thread CurrentThread {
			get {
				if (IsProcessRunning) throw new CurrentThreadNotAviableException();
				if (currentThread != null) return currentThread;
				if (mainThread != null) return mainThread;
				throw new CurrentThreadNotAviableException();
			}
			set	{
				currentThread = value;
				if (mainThread == null) {
					mainThread = value;
				}
				if (NDebugger.ManagedCallback.HandlingCallback == false) {
					NDebugger.OnDebuggingPaused(PausedReason.CurrentThreadChanged);
				}
			}
		}

		public Thread MainThread {
			get {
				return mainThread;
			}
			set {
				mainThread = value;
			}
		}

		public SourcecodeSegment NextStatement { 
			get{
				try {
					return CurrentThread.NextStatement; 
				} catch (CurrentThreadNotAviableException) {
					System.Diagnostics.Debug.Fail("Unable to get NextStatement. CurrentThreadNotAviableException");
					throw new NextStatementNotAviableException();
				}
			} 
		}

		public VariableCollection LocalVariables { 
			get{
				Thread thread;
				try {
					thread = CurrentThread;
				} 
				catch (CurrentThreadNotAviableException) {
					//System.Diagnostics.Debug.Fail("Unable to get LocalVariables. CurrentThreadNotAviableException");
					return new VariableCollection ();
				}
				return thread.LocalVariables;
			} 
		}
		
		static public Process CreateProcess(string filename, string workingDirectory, string arguments)
		{
			MTA2STA m2s = new MTA2STA();
			Process createdProcess = null;
			createdProcess = (Process)m2s.CallInSTA(typeof(Process), "StartInternal", new Object[] {filename, workingDirectory, arguments});
			return createdProcess;
		}

		static public unsafe Process StartInternal(string filename, string workingDirectory, string arguments)
		{
			NDebugger.TraceMessage("Executing " + filename);

			_SECURITY_ATTRIBUTES secAttr = new _SECURITY_ATTRIBUTES();
			secAttr.bInheritHandle = 0;
			secAttr.lpSecurityDescriptor = IntPtr.Zero;
			secAttr.nLength = (uint)sizeof(_SECURITY_ATTRIBUTES); //=12?

			uint[] processStartupInfo = new uint[17];
			processStartupInfo[0] = sizeof(uint) * 17;
			uint[] processInfo = new uint[4];

			ICorDebugProcess outProcess;

			fixed (uint* pprocessStartupInfo = processStartupInfo)
				fixed (uint* pprocessInfo = processInfo)
					NDebugger.CorDebug.CreateProcess(
						filename,   // lpApplicationName
						arguments,                       // lpCommandLine
						ref secAttr,                       // lpProcessAttributes
						ref secAttr,                      // lpThreadAttributes
						1,//TRUE                    // bInheritHandles
						0,                          // dwCreationFlags
						IntPtr.Zero,                       // lpEnvironment
                        workingDirectory,                       // lpCurrentDirectory
						(uint)pprocessStartupInfo,        // lpStartupInfo
						(uint)pprocessInfo,               // lpProcessInformation,
						CorDebugCreateProcessFlags.DEBUG_NO_SPECIAL_OPTIONS,   // debuggingFlags
						out outProcess      // ppProcess
						);

			return new Process(outProcess);
		}

		public void Break()
		{
			if (!IsProcessRunning) {
				System.Diagnostics.Debug.Fail("Invalid operation");
				return;
			}

            corProcess.Stop(5000); // TODO: Hardcoded value

			isProcessRunning = false;
			NDebugger.OnDebuggingPaused(PausedReason.Break);
			NDebugger.OnIsProcessRunningChanged();
		}

		public void StepInto()
		{
			try {
				CurrentThread.StepInto();
			} catch (CurrentThreadNotAviableException) {
				System.Diagnostics.Debug.Fail("Unable to prerform step. CurrentThreadNotAviableException");
			}
		}

		public void StepOver()
		{
			try {
				CurrentThread.StepOver();
			} catch (CurrentThreadNotAviableException) {
				System.Diagnostics.Debug.Fail("Unable to prerform step. CurrentThreadNotAviableException");
			}
		}

		public void StepOut()
		{
			try {
				CurrentThread.StepOut();
			} catch (CurrentThreadNotAviableException) {
				System.Diagnostics.Debug.Fail("Unable to prerform step. CurrentThreadNotAviableException");
			}
		}

		public void Continue()
		{
			if (IsProcessRunning) {
				System.Diagnostics.Debug.Fail("Invalid operation");
				return;
			}

			bool abort = false;
			NDebugger.OnDebuggingIsResuming(ref abort);
			if (abort == true) return;

			isProcessRunning = true;
			if (NDebugger.ManagedCallback.HandlingCallback == false) {
				NDebugger.OnDebuggingResumed();
				NDebugger.OnIsProcessRunningChanged();
			}

			corProcess.Continue(0);
		}

		public void Terminate()
		{
			int running;
			corProcess.IsRunning(out running);
			// Resume stoped tread
			if (running == 0) {
				Continue(); // TODO: Remove this...
			}
			// Stop&terminate - both must be called
			corProcess.Stop(5000); // TODO: ...and this
			corProcess.Terminate(0);
		}

		public bool IsProcessRunning { 
			get {
				return isProcessRunning;
			}
			set {
				isProcessRunning = value;
			}
		}
	}
}
