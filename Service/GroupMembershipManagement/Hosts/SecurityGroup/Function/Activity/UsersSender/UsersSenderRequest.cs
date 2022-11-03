// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Entities;
using System;
using System.Collections.Generic;

namespace Hosts.SecurityGroup
{
    public class UsersSenderRequest
    {
        public SyncJob SyncJob { get; set; }
        public Guid RunId { get; set; }
        public List<AzureADUser> Users { get; set; }
        public int CurrentPart { get; set; }
        public bool Exclusionary { get; set; }
    }
}