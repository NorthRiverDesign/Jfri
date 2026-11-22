using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jfri.Web.Data.Migrations
{
	/// <inheritdoc />
	public partial class InitialCreate : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.CreateTable(
				name: "Admins",
				columns: table => new
				{
					Id = table.Column<int>(type: "INTEGER", nullable: false)
						.Annotation("Sqlite:Autoincrement", true),
					UserName = table.Column<string>(type: "TEXT", nullable: false),
					PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
					CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Admins", x => x.Id);
				});

			migrationBuilder.CreateTable(
				name: "Connections",
				columns: table => new
				{
					Id = table.Column<int>(type: "INTEGER", nullable: false)
						.Annotation("Sqlite:Autoincrement", true),
					Provider = table.Column<string>(type: "TEXT", nullable: false),
					Name = table.Column<string>(type: "TEXT", nullable: false),
					EncryptedToken = table.Column<string>(type: "TEXT", nullable: false),
					Hint = table.Column<string>(type: "TEXT", nullable: false),
					CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Connections", x => x.Id);
				});

			migrationBuilder.CreateTable(
				name: "Servers",
				columns: table => new
				{
					Id = table.Column<int>(type: "INTEGER", nullable: false)
						.Annotation("Sqlite:Autoincrement", true),
					Name = table.Column<string>(type: "TEXT", nullable: false),
					Provider = table.Column<string>(type: "TEXT", nullable: false),
					ConnectionId = table.Column<int>(type: "INTEGER", nullable: true),
					ProviderServerId = table.Column<string>(type: "TEXT", nullable: true),
					Region = table.Column<string>(type: "TEXT", nullable: true),
					Size = table.Column<string>(type: "TEXT", nullable: true),
					Host = table.Column<string>(type: "TEXT", nullable: false),
					Port = table.Column<int>(type: "INTEGER", nullable: false),
					User = table.Column<string>(type: "TEXT", nullable: false),
					EncryptedPrivateKey = table.Column<string>(type: "TEXT", nullable: false),
					PublicKey = table.Column<string>(type: "TEXT", nullable: false),
					KnownHosts = table.Column<string>(type: "TEXT", nullable: true),
					Runtime = table.Column<string>(type: "TEXT", nullable: true),
					Status = table.Column<string>(type: "TEXT", nullable: false),
					CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Servers", x => x.Id);
					table.ForeignKey(
						name: "FK_Servers_Connections_ConnectionId",
						column: x => x.ConnectionId,
						principalTable: "Connections",
						principalColumn: "Id",
						onDelete: ReferentialAction.SetNull);
				});

			migrationBuilder.CreateTable(
				name: "Apps",
				columns: table => new
				{
					Id = table.Column<int>(type: "INTEGER", nullable: false)
						.Annotation("Sqlite:Autoincrement", true),
					Name = table.Column<string>(type: "TEXT", nullable: false),
					RepoUrl = table.Column<string>(type: "TEXT", nullable: false),
					Branch = table.Column<string>(type: "TEXT", nullable: true),
					EncryptedGitToken = table.Column<string>(type: "TEXT", nullable: true),
					Commit = table.Column<string>(type: "TEXT", nullable: true),
					ProfileJson = table.Column<string>(type: "TEXT", nullable: false),
					OptionsJson = table.Column<string>(type: "TEXT", nullable: false),
					EncryptedSecrets = table.Column<string>(type: "TEXT", nullable: true),
					EncryptedUserSecrets = table.Column<string>(type: "TEXT", nullable: true),
					EncryptedWebhookSecret = table.Column<string>(type: "TEXT", nullable: false),
					ServerId = table.Column<int>(type: "INTEGER", nullable: true),
					LiveUrl = table.Column<string>(type: "TEXT", nullable: true),
					CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
					AnalyzedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Apps", x => x.Id);
					table.ForeignKey(
						name: "FK_Apps_Servers_ServerId",
						column: x => x.ServerId,
						principalTable: "Servers",
						principalColumn: "Id",
						onDelete: ReferentialAction.SetNull);
				});

			migrationBuilder.CreateTable(
				name: "Deployments",
				columns: table => new
				{
					Id = table.Column<int>(type: "INTEGER", nullable: false)
						.Annotation("Sqlite:Autoincrement", true),
					AppId = table.Column<int>(type: "INTEGER", nullable: false),
					ServerId = table.Column<int>(type: "INTEGER", nullable: true),
					Status = table.Column<string>(type: "TEXT", nullable: false),
					Kind = table.Column<string>(type: "TEXT", nullable: false),
					Trigger = table.Column<string>(type: "TEXT", nullable: false),
					NewServerJson = table.Column<string>(type: "TEXT", nullable: true),
					Commit = table.Column<string>(type: "TEXT", nullable: true),
					Revision = table.Column<string>(type: "TEXT", nullable: true),
					PlanJson = table.Column<string>(type: "TEXT", nullable: true),
					Url = table.Column<string>(type: "TEXT", nullable: true),
					Error = table.Column<string>(type: "TEXT", nullable: true),
					CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
					StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
					FinishedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_Deployments", x => x.Id);
					table.ForeignKey(
						name: "FK_Deployments_Apps_AppId",
						column: x => x.AppId,
						principalTable: "Apps",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_Admins_UserName",
				table: "Admins",
				column: "UserName",
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_Apps_Name",
				table: "Apps",
				column: "Name",
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_Apps_ServerId",
				table: "Apps",
				column: "ServerId");

			migrationBuilder.CreateIndex(
				name: "IX_Deployments_AppId_CreatedAt",
				table: "Deployments",
				columns: new[] { "AppId", "CreatedAt" });

			migrationBuilder.CreateIndex(
				name: "IX_Servers_ConnectionId",
				table: "Servers",
				column: "ConnectionId");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(
				name: "Admins");

			migrationBuilder.DropTable(
				name: "Deployments");

			migrationBuilder.DropTable(
				name: "Apps");

			migrationBuilder.DropTable(
				name: "Servers");

			migrationBuilder.DropTable(
				name: "Connections");
		}
	}
}
