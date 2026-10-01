ARG BASE_IMAGE=mcr.microsoft.com/mssql/server:2022-latest
FROM ${BASE_IMAGE}

ARG SQL_MAJOR=2022

USER root

RUN . /etc/os-release && \
    echo "deb [arch=amd64 trusted=yes] https://packages.microsoft.com/ubuntu/${VERSION_ID}/mssql-server-${SQL_MAJOR} ${VERSION_CODENAME} main" > /etc/apt/sources.list.d/mssql-server-${SQL_MAJOR}.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends mssql-server-fts && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

USER mssql
