﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
/*=============================================================================
**
**
** Purpose: Contains definitions for supporting Exception Notifications.
**
** 
=============================================================================*/
using System;

namespace System.Runtime.ExceptionServices
{
    // Definition of the argument-type passed to the FirstChanceException event handler
    public class FirstChanceExceptionEventArgs : EventArgs
    {
        // Constructor
        public FirstChanceExceptionEventArgs(Exception exception)
        {
            _exception = exception;
        }

        // Returns the exception object pertaining to the first chance exception
        public Exception Exception
        {
            get { return _exception; }
        }

        // Represents the FirstChance exception instance
        private Exception _exception;
    }
}
