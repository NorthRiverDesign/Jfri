using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jfri.Web.Data.Migrations
{
	/// <inheritdoc />
	public partial class DeploymentKindAndPlan : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<string>(
				name: "Kind",
				table: "Deployments",
				type: "TEXT",
				nullable: false,
				defaultValue: "deploy");

			migrationBuilder.AddColumn<string>(
				name: "PlanJson",
				table: "Deployments",
				type: "TEXT",
				nullable: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn(
				name: "Kind",
				table: "Deployments");

			migrationBuilder.DropColumn(
				name: "PlanJson",
				table: "Deployments");
		}
	}
}
