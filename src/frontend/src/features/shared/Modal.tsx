const Modal = ({
  open,
  title,
  onClose,
  children,
  wide,
  footer,
}: {
  open: boolean
  title: string
  onClose: () => void
  children: React.ReactNode
  wide?: boolean
  footer?: React.ReactNode
}) => {
  if (!open) {
    return null
  }
  return (
    <>
      <div className="modal-backdrop" onClick={onClose} />
      <div className={`modal-panel${wide ? ' modal-wide' : ''}`}>
        <div className="modal-header">
          <div className="modal-title">{title}</div>
          <button type="button" className="btn btn-outline-secondary btn-sm" onClick={onClose}>
            关闭
          </button>
        </div>
        <div className="modal-body">{children}</div>
        {footer ? <div className="modal-footer">{footer}</div> : null}
      </div>
    </>
  )
}

export default Modal
