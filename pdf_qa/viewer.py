"""Zoomable / pannable page view with rubber-band selection and overlay layers.

Scene coordinates == PDF display coordinates (points). The rendered pixmap is
scaled by 1/zoom so overlays can be drawn directly in PDF points.
"""
from __future__ import annotations

from collections import defaultdict

import numpy as np
from PySide6.QtCore import Qt, QRectF, QPointF, QPoint, Signal
from PySide6.QtGui import (QBrush, QColor, QFont, QImage, QPainter, QPen, QPixmap)
from PySide6.QtWidgets import (QGraphicsRectItem, QGraphicsScene, QGraphicsSimpleTextItem,
                               QGraphicsView, QGraphicsDropShadowEffect)


def np_to_qimage(arr: np.ndarray) -> QImage:
    arr = np.ascontiguousarray(arr)
    h, w = arr.shape[:2]
    if arr.ndim == 2:
        img = QImage(arr.data, w, h, arr.strides[0], QImage.Format_Grayscale8)
    else:
        img = QImage(arr.data, w, h, arr.strides[0], QImage.Format_RGB888)
    return img.copy()


class HighlightItem(QGraphicsRectItem):
    """Highlighter-pen look: multiply blend keeps linework/text dark under the colour."""

    def paint(self, painter, option, widget=None):
        painter.save()
        painter.setCompositionMode(QPainter.CompositionMode_Multiply)
        painter.setPen(Qt.NoPen)
        painter.setBrush(self.brush())
        painter.drawRect(self.rect())
        painter.restore()


class PdfView(QGraphicsView):
    rectSelected = Signal(QRectF)
    viewChanged = Signal()
    mouseMovedPdf = Signal(QPointF)
    contextAt = Signal(QPointF, QPoint)

    def __init__(self, title: str = "", accent: str = "#4f8cff", parent=None):
        super().__init__(parent)
        self.setScene(QGraphicsScene(self))
        self.title = title
        self.subtitle = ""
        self.accent = QColor(accent)
        self.placeholder = "Chưa mở file"
        self.pix_item = None
        self.page_rect = QRectF()
        self.layers: dict[str, list] = defaultdict(list)
        self.tool = "pan"
        self.sel_color = QColor("#ffcc00")
        self._sel_item = None
        self._sel_origin = None
        self._mid_pan = None

        self.setRenderHints(QPainter.Antialiasing | QPainter.SmoothPixmapTransform)
        self.setTransformationAnchor(QGraphicsView.AnchorUnderMouse)
        self.setResizeAnchor(QGraphicsView.AnchorViewCenter)
        self.setViewportUpdateMode(QGraphicsView.FullViewportUpdate)
        self.setBackgroundBrush(QColor("#12151c"))
        self.setFrameShape(QGraphicsView.NoFrame)
        self.setMouseTracking(True)
        self.set_tool("pan")
        self.horizontalScrollBar().valueChanged.connect(lambda _: self.viewChanged.emit())
        self.verticalScrollBar().valueChanged.connect(lambda _: self.viewChanged.emit())

    # ------------------------------------------------------------------ image
    def set_image(self, img: QImage | None, scale: float, page_rect: QRectF, keep_view=True):
        old_t = self.transform()
        old_c = self.mapToScene(self.viewport().rect().center())
        had = self.pix_item is not None
        old_size = self.page_rect.size()
        self.scene().clear()
        self.layers.clear()
        self._sel_item = None
        self.pix_item = None
        self.page_rect = QRectF(page_rect)
        if img is None:
            self.scene().setSceneRect(QRectF(0, 0, 10, 10))
            self.viewport().update()
            return
        off = max(page_rect.width(), page_rect.height()) * 0.004
        shadow = QGraphicsRectItem(page_rect.translated(off, off))
        shadow.setBrush(QColor(0, 0, 0, 120))
        shadow.setPen(QPen(Qt.NoPen))
        self.scene().addItem(shadow)
        self.pix_item = self.scene().addPixmap(QPixmap.fromImage(img))
        self.pix_item.setTransformationMode(Qt.SmoothTransformation)
        self.pix_item.setScale(1.0 / scale)
        self.pix_item.setPos(page_rect.topLeft())
        self.pix_item.setZValue(0)
        shadow.setZValue(-1)
        m = max(page_rect.width(), page_rect.height()) * 0.15
        self.scene().setSceneRect(page_rect.adjusted(-m, -m, m, m))
        same = abs(old_size.width() - page_rect.width()) < 2 and abs(old_size.height() - page_rect.height()) < 2
        if keep_view and had and same:
            self.setTransform(old_t)
            self.centerOn(old_c)
        else:
            self.fit()

    def fit(self):
        if not self.page_rect.isNull():
            self.fitInView(self.page_rect.adjusted(-10, -10, 10, 10), Qt.KeepAspectRatio)
            self.viewChanged.emit()

    def zoom_by(self, f: float):
        self.setTransformationAnchor(QGraphicsView.AnchorViewCenter)
        self.scale(f, f)
        self.setTransformationAnchor(QGraphicsView.AnchorUnderMouse)
        self.viewChanged.emit()

    def zoom_to(self, rect: QRectF, margin_ratio: float = 1.5):
        if rect.isNull():
            return
        w = max(rect.width(), 30) * margin_ratio
        h = max(rect.height(), 30) * margin_ratio
        c = rect.center()
        self.fitInView(QRectF(c.x() - w / 2, c.y() - h / 2, w, h), Qt.KeepAspectRatio)
        self.viewChanged.emit()

    def zoom_factor(self) -> float:
        return self.transform().m11()

    def sync_from(self, other: "PdfView"):
        self.setTransform(other.transform())
        self.centerOn(other.mapToScene(other.viewport().rect().center()))

    # --------------------------------------------------------------- overlays
    def clear_layer(self, tag: str):
        for it in self.layers.pop(tag, []):
            if it.scene() is self.scene():
                self.scene().removeItem(it)

    def add_pixmap(self, tag: str, img: QImage, scale: float, pos: QPointF, z: float = 1):
        if self.pix_item is None:
            return None
        it = self.scene().addPixmap(QPixmap.fromImage(img))
        it.setTransformationMode(Qt.SmoothTransformation)
        it.setScale(1.0 / scale)
        it.setPos(pos)
        it.setZValue(z)
        self.layers[tag].append(it)
        return it

    def add_highlight(self, tag: str, rect: QRectF, color: QColor, opacity: float, z: float = 6):
        if self.pix_item is None:
            return None
        it = HighlightItem(rect)
        c = QColor(color)
        c.setAlphaF(max(0.05, min(1.0, opacity)))
        it.setBrush(QBrush(c))
        it.setZValue(z)
        self.scene().addItem(it)
        self.layers[tag].append(it)
        return it

    def contextMenuEvent(self, e):
        if self.pix_item is not None:
            self.contextAt.emit(self.mapToScene(e.pos()), e.globalPos())

    def add_rect(self, tag: str, rect: QRectF, color: QColor, fill_alpha: int = 50,
                 width: float = 2.0, label: str | None = None, dash=False, z: float = 10):
        if self.pix_item is None:
            return None
        pen = QPen(color, width)
        pen.setCosmetic(True)
        if dash:
            pen.setStyle(Qt.DashLine)
        item = QGraphicsRectItem(rect)
        item.setPen(pen)
        fc = QColor(color)
        fc.setAlpha(fill_alpha)
        item.setBrush(QBrush(fc))
        item.setZValue(z)
        self.scene().addItem(item)
        self.layers[tag].append(item)
        if label:
            t = QGraphicsSimpleTextItem(label)
            t.setBrush(QBrush(color))
            f = QFont("Segoe UI", 9, QFont.Bold)
            t.setFont(f)
            t.setFlag(QGraphicsSimpleTextItem.ItemIgnoresTransformations, True)
            t.setPos(rect.left(), rect.top())
            t.setZValue(z + 1)
            bg = QGraphicsRectItem(t.boundingRect().adjusted(-3, -1, 3, 1), t)
            bg.setBrush(QColor(15, 18, 25, 210))
            bg.setPen(QPen(Qt.NoPen))
            bg.setFlag(QGraphicsRectItem.ItemStacksBehindParent, True)
            t.moveBy(0, -16)
            self.scene().addItem(t)
            self.layers[tag].append(t)
        return item

    # ------------------------------------------------------------------ tools
    def set_tool(self, tool: str, color: QColor | None = None):
        self.tool = tool
        if color is not None:
            self.sel_color = QColor(color)
        if tool == "pan":
            self.setDragMode(QGraphicsView.ScrollHandDrag)
            self.viewport().setCursor(Qt.OpenHandCursor)
        else:
            self.setDragMode(QGraphicsView.NoDrag)
            self.viewport().setCursor(Qt.CrossCursor)

    def wheelEvent(self, e):
        f = 1.0015 ** e.angleDelta().y()
        z = self.zoom_factor() * f
        if 0.02 < z < 80:
            self.scale(f, f)
            self.viewChanged.emit()

    def mousePressEvent(self, e):
        if e.button() == Qt.MiddleButton:
            self._mid_pan = e.position()
            self.viewport().setCursor(Qt.ClosedHandCursor)
            return
        if self.tool != "pan" and e.button() == Qt.LeftButton and self.pix_item is not None:
            self._sel_origin = self.mapToScene(e.position().toPoint())
            pen = QPen(self.sel_color, 1.5, Qt.DashLine)
            pen.setCosmetic(True)
            self._sel_item = QGraphicsRectItem(QRectF(self._sel_origin, self._sel_origin))
            self._sel_item.setPen(pen)
            c = QColor(self.sel_color)
            c.setAlpha(40)
            self._sel_item.setBrush(c)
            self._sel_item.setZValue(100)
            self.scene().addItem(self._sel_item)
            return
        super().mousePressEvent(e)

    def mouseMoveEvent(self, e):
        p = self.mapToScene(e.position().toPoint())
        self.mouseMovedPdf.emit(p)
        if self._mid_pan is not None:
            d = e.position() - self._mid_pan
            self._mid_pan = e.position()
            self.horizontalScrollBar().setValue(self.horizontalScrollBar().value() - int(d.x()))
            self.verticalScrollBar().setValue(self.verticalScrollBar().value() - int(d.y()))
            return
        if self._sel_item is not None:
            self._sel_item.setRect(QRectF(self._sel_origin, p).normalized())
            return
        super().mouseMoveEvent(e)

    def mouseReleaseEvent(self, e):
        if e.button() == Qt.MiddleButton and self._mid_pan is not None:
            self._mid_pan = None
            self.set_tool(self.tool)
            return
        if self._sel_item is not None and e.button() == Qt.LeftButton:
            r = self._sel_item.rect().intersected(self.page_rect)
            self.scene().removeItem(self._sel_item)
            self._sel_item = None
            px = r.width() * self.zoom_factor(), r.height() * self.zoom_factor()
            if px[0] > 4 and px[1] > 4:
                self.rectSelected.emit(r)
            return
        super().mouseReleaseEvent(e)

    # -------------------------------------------------------------- painting
    def drawForeground(self, painter: QPainter, rect):
        painter.save()
        painter.resetTransform()
        painter.setRenderHint(QPainter.Antialiasing)
        vr = self.viewport().rect()
        if self.pix_item is None:
            painter.setPen(QColor("#5b6578"))
            painter.setFont(QFont("Segoe UI", 13))
            painter.drawText(vr, Qt.AlignCenter, self.placeholder)
        if self.title:
            f = QFont("Segoe UI", 9, QFont.Bold)
            painter.setFont(f)
            txt = self.title + (f"  ·  {self.subtitle}" if self.subtitle else "")
            fm = painter.fontMetrics()
            w = min(fm.horizontalAdvance(txt) + 44, vr.width() - 20)
            badge = QRectF(12, 12, w, 26)
            painter.setPen(Qt.NoPen)
            painter.setBrush(QColor(18, 22, 30, 225))
            painter.drawRoundedRect(badge, 13, 13)
            painter.setBrush(self.accent)
            painter.drawEllipse(QPointF(badge.left() + 13, badge.center().y()), 4, 4)
            painter.setPen(QColor("#e6e9ef"))
            painter.drawText(badge.adjusted(24, 0, -8, 0), Qt.AlignVCenter | Qt.AlignLeft,
                             fm.elidedText(txt, Qt.ElideMiddle, int(badge.width() - 32)))
        painter.restore()
