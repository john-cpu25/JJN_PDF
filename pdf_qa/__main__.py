"""Entry point:  python -m pdf_qa [ver1.pdf] [ver2.pdf]"""
import sys

from PySide6.QtGui import QColor, QPalette
from PySide6.QtWidgets import QApplication

from .main_window import QSS, MainWindow


def apply_theme(app: QApplication):
    app.setStyle("Fusion")
    pal = QPalette()
    for role, col in [(QPalette.Window, "#12151c"), (QPalette.WindowText, "#d7dce5"),
                      (QPalette.Base, "#1b202b"), (QPalette.AlternateBase, "#1e2430"),
                      (QPalette.Text, "#d7dce5"), (QPalette.Button, "#232a37"),
                      (QPalette.ButtonText, "#d7dce5"), (QPalette.Highlight, "#2b4a8a"),
                      (QPalette.HighlightedText, "#ffffff"), (QPalette.ToolTipBase, "#232a37"),
                      (QPalette.ToolTipText, "#e6e9ef")]:
        pal.setColor(role, QColor(col))
    app.setPalette(pal)
    app.setStyleSheet(QSS)


def main():
    app = QApplication(sys.argv)
    app.setApplicationName("PDF QA Viewer")
    apply_theme(app)
    w = MainWindow()
    geo = app.primaryScreen().availableGeometry()
    w.resize(min(1680, int(geo.width() * 0.95)), min(980, int(geo.height() * 0.92)))
    w.showMaximized()
    args = [a for a in sys.argv[1:] if a.lower().endswith(".pdf")]
    if args:
        w.open_doc(1, args[0])
    if len(args) > 1:
        w.open_doc(2, args[1])
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
