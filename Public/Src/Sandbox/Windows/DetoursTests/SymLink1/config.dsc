// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

config({
    resolvers: [
        {
            kind: "DScript",
            modules: [
                f`module.config.dsc`,
                f`../../../../../Sdk/Public/Prelude/Package.config.dsc`,
                f`../../../../../Sdk/Public/Transformers/package.config.dsc`,
            ]
        }
    ],
    qualifiers: {
        defaultQualifier: {
            targetRuntime: 
                Context.getCurrentHost().os === "win" ? "win-x64" :
                Context.getCurrentHost().os === "macOS" ? "osx-x64" : "linux-x64",
        },
    }
}); 
