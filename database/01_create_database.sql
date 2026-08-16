-- AI Receptionist Platform — creates the AiDB database.
-- Run against the master database.
IF DB_ID('AiDB') IS NULL
    CREATE DATABASE AiDB;
GO
