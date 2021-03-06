// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace System.Threading
{
    public partial class EventWaitHandle
    {
        private EventWaitHandle(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }

        private static void VerifyNameForCreate(string name)
        {
            if (null != name && ((int)Interop.Constants.MaxPath) < name.Length)
            {
                throw new ArgumentException(SR.Format(SR.Argument_WaitHandleNameTooLong, Interop.Constants.MaxPath), nameof(name));
            }
        }

        private void CreateEventCore(bool initialState, EventResetMode mode, string name, out bool createdNew)
        {
            Debug.Assert(Enum.IsDefined(typeof(EventResetMode), mode));
            Debug.Assert(name == null || name.Length <= (int)Interop.Constants.MaxPath);

            uint eventFlags = initialState ? (uint)Interop.Constants.CreateEventInitialSet : 0;
            if (mode == EventResetMode.ManualReset)
            {
                eventFlags |= (uint)Interop.Constants.CreateEventManualReset;
            }

            IntPtr unsafeHandle = Interop.mincore.CreateEventEx(IntPtr.Zero, name, eventFlags, (uint)Interop.Constants.EventAllAccess);
            SafeWaitHandle _handle = new SafeWaitHandle(unsafeHandle, true);

            if (_handle.IsInvalid)
            {
                int errorCode = (int)Interop.mincore.GetLastError();
                _handle.SetHandleAsInvalid();
                if (null != name && 0 != name.Length && Interop.mincore.Errors.ERROR_INVALID_HANDLE == errorCode)
                    throw new WaitHandleCannotBeOpenedException(SR.Format(SR.Threading_WaitHandleCannotBeOpenedException_InvalidHandle, name));
                throw ExceptionFromCreationError(errorCode, name);
            }
            else if (name != null)
            {
                int errorCode = (int)Interop.mincore.GetLastError();
                createdNew = errorCode != Interop.mincore.Errors.ERROR_ALREADY_EXISTS;
            }
            else
            {
                createdNew = true;
            }

            SafeWaitHandle = _handle;
        }

        private static OpenExistingResult OpenExistingWorker(string name, out EventWaitHandle result)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name), SR.ArgumentNull_WithParamName);
            }

            if (name.Length == 0)
            {
                throw new ArgumentException(SR.Argument_EmptyName, nameof(name));
            }

            if (null != name && ((int)Interop.Constants.MaxPath) < name.Length)
            {
                throw new ArgumentException(SR.Format(SR.Argument_WaitHandleNameTooLong, Interop.Constants.MaxPath), nameof(name));
            }

            Contract.EndContractBlock();

            result = null;

            IntPtr unsafeHandle = Interop.mincore.OpenEvent((uint)(Interop.Constants.EventModifyState | Interop.Constants.Synchronize), false, name);

            int errorCode = (int)Interop.mincore.GetLastError();
            SafeWaitHandle myHandle = new SafeWaitHandle(unsafeHandle, true);

            if (myHandle.IsInvalid)
            {
                if (Interop.mincore.Errors.ERROR_FILE_NOT_FOUND == errorCode || Interop.mincore.Errors.ERROR_INVALID_NAME == errorCode)
                    return OpenExistingResult.NameNotFound;
                if (Interop.mincore.Errors.ERROR_PATH_NOT_FOUND == errorCode)
                    return OpenExistingResult.PathNotFound;
                if (null != name && 0 != name.Length && Interop.mincore.Errors.ERROR_INVALID_HANDLE == errorCode)
                    return OpenExistingResult.NameInvalid;
                //this is for passed through Win32Native Errors
                throw ExceptionFromCreationError(errorCode, name);
            }
            result = new EventWaitHandle(myHandle);
            return OpenExistingResult.Success;
        }

        private static bool ResetCore(IntPtr handle)
        {
            bool res = Interop.mincore.ResetEvent(handle);
            if (!res)
                ThrowSignalOrUnsignalException();
            return res;
        }

        private static bool SetCore(IntPtr handle)
        {
            bool res = Interop.mincore.SetEvent(handle);
            if (!res)
                ThrowSignalOrUnsignalException();
            return res;
        }
    }
}
