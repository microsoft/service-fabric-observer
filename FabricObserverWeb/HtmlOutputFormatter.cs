// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace FabricObserverWeb
{
    using Microsoft.AspNetCore.Mvc.Formatters;

    // Added to support HTML output to callers...
    public class HtmlOutputFormatter : StringOutputFormatter
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="HtmlOutputFormatter"/> class.
        /// </summary>
        public HtmlOutputFormatter()
        {
            this.SupportedMediaTypes.Add("text/html");
        }
    }
}
