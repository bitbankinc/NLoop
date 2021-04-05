#!/usr/bin/env bash

dotnet nswag openapi2csclient /namespace:NLoop.OpenAPIClient /input:openapi.yml /output:NLoop.OpenAPIClient/Client.cs

