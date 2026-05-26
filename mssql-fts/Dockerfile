FROM mcr.microsoft.com/mssql/server:2022-latest

USER root

RUN echo 'deb [arch=amd64 trusted=yes] https://packages.microsoft.com/ubuntu/22.04/mssql-server-2022 jammy main' > /etc/apt/sources.list.d/mssql-server-2022.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends mssql-server-fts && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

USER mssql
