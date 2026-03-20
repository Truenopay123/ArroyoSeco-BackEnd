using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace arroyoSeco.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModeracionResenasPorReporte : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Cambiar el valor por defecto de Estado de "Pendiente" a "publicada" ────────────
            migrationBuilder.AlterColumn<string>(
                name: "Estado",
                table: "Resenas",
                type: "text",
                nullable: false,
                defaultValue: "publicada",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "Pendiente");

            // ── Agregar campos para el sistema de reportes ────────────────────────────────────
            migrationBuilder.AddColumn<string>(
                name: "MotivoReporte",
                table: "Resenas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaReporte",
                table: "Resenas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OfferenteIdQueReporto",
                table: "Resenas",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ── Revertir cambios ───────────────────────────────────────────────────────────
            migrationBuilder.DropColumn(
                name: "OfferenteIdQueReporto",
                table: "Resenas");

            migrationBuilder.DropColumn(
                name: "FechaReporte",
                table: "Resenas");

            migrationBuilder.DropColumn(
                name: "MotivoReporte",
                table: "Resenas");

            migrationBuilder.AlterColumn<string>(
                name: "Estado",
                table: "Resenas",
                type: "text",
                nullable: false,
                defaultValue: "Pendiente",
                oldClrType: typeof(string),
                oldType: "text",
                oldDefaultValue: "publicada");
        }
    }
}
