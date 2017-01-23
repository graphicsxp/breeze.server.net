using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace Breeze.Query {
  public class LitExpression : BaseExpression {

    private Object _initialValue;
    private Object _coercedValue;
    private DataType _dataType;

    // TODO: doesn't yet handle case where value is an array - i.e. rhs of in clause.
    public LitExpression(Object value, DataType dataType) {
      _initialValue = value;
      _dataType = dataType;
      _coercedValue = DataType.CoerceData(value, dataType);
    }

    public Object GetValue() {
      return _coercedValue;
    }

    public override DataType DataType {
      get { return _dataType; }
    }

    public override Expression ToExpression(Expression inExpr) {
      return Expression.Constant(_coercedValue);
    }

  }
}