// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.ObjectModel;
using Microsoft.Framework.Internal;

namespace Microsoft.Framework.TestAdapter
{
    public sealed class TestResult
    {
        public TestResult([NotNull] Test test)
        {
            Test = test;
            Messages = new Collection<string>();
        }

        public Test Test { get; private set; }

        public TestOutcome Outcome { get; set; }

        public string ErrorMessage { get; set; }

        public string ErrorStackTrace { get; set; }

        public string DisplayName { get; set; }

        public Collection<string> Messages { get;  private set; }

        public string ComputerName { get; set; }

        public TimeSpan Duration { get; set; }

        public DateTimeOffset StartTime { get; set; }

        public DateTimeOffset EndTime { get; set; }
    }
}